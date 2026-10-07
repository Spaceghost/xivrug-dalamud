using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothClearanceTraceTests
{
    private static readonly Vector3[] Ridge = [new(0,.18f,0), new(1,.68f,0), new(2,.18f,0), new(3,.18f,0)];
    private static readonly Vector3 Origin = Vector3.Zero;
    private static readonly Vector3 Target = new(3,0,0);

    [Fact]
    public void SmallBudgetResumesAfterCrestAndFindsWallWithoutRepeatingClearPrefix()
    {
        var trace = new ClothClearanceTrace(); var tested = new List<Vector3>();
        var remaining = 1;
        var first = trace.Query(1, Origin, Target, Ridge, 0, Cast, out _);
        Assert.Equal(ClothClearanceResult.Pending, first);
        Assert.Single(tested); Assert.Equal(Ridge[0], tested[0]);
        remaining = 1;
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Origin, Target, Ridge, .1, Cast, out _));
        remaining = 1;
        Assert.Equal(ClothClearanceResult.Blocked, trace.Query(1, Origin, Target, Ridge, .2, Cast, out var point));
        Assert.Equal(new(2.5f,.18f,0), point);
        Assert.Equal(Ridge[..3], tested); Assert.Equal(0, trace.PendingCount);

        ClothClearanceCast Cast(Vector3 from, Vector3 to)
        {
            if (remaining-- <= 0) return new(ClothClearanceResult.Pending);
            tested.Add(from);
            return from.X == 2 ? new(ClothClearanceResult.Blocked, (from + to) / 2) : new(ClothClearanceResult.Clear);
        }
    }

    [Fact]
    public void EntirePolylineCanResumeAndCompletedRefreshRequiresNewRays()
    {
        var trace = new ClothClearanceTrace(); var clearCalls = 0; var allowance = 1;
        ClothClearanceCast Cast(Vector3 from, Vector3 to) => allowance-- > 0
            ? CountClear() : new(ClothClearanceResult.Pending);
        ClothClearanceCast CountClear() { clearCalls++; return new(ClothClearanceResult.Clear); }
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Origin, Target, Ridge, 0, Cast, out _));
        allowance = 2;
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Origin, Target, Ridge, .1, Cast, out _));
        Assert.Equal(3, clearCalls); Assert.Equal(0, trace.PendingCount);
        allowance = 3;
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Origin, Target, Ridge, .2, Cast, out _));
        Assert.Equal(6, clearCalls);
    }

    [Fact]
    public void ChangedIntermediateGeometryInvalidatesThePreviouslyClearPrefix()
    {
        var trace = new ClothClearanceTrace(); Prime(trace);
        var changed = Ridge.ToArray(); changed[1].Y += .02f;
        var from = new List<Vector3>();
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Origin, Target, changed, .1,
            (a, _) => { from.Add(a); return new(ClothClearanceResult.Clear); }, out _));
        Assert.Equal(changed[..3], from);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerationOrMeasuredEndpointHeightDoesNotReuseOldClearance(bool generationChanges)
    {
        var trace = new ClothClearanceTrace(); Prime(trace);
        var path = Ridge.ToArray(); var origin = Origin; var target = Target;
        if (!generationChanges) { origin.Y = .01f; target.Y = .01f; }
        var tested = new List<Vector3>();
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(generationChanges ? 2 : 1, origin, target, path, .1,
            (a, _) => { tested.Add(a); return new(ClothClearanceResult.Clear); }, out _));
        Assert.Equal(Ridge[..3], tested);
    }

    [Fact]
    public void RepeatedPendingRequestsCannotRenewTheAgeOfOldClearanceEvidence()
    {
        var trace = new ClothClearanceTrace(); Prime(trace);
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Origin, Target, Ridge, .4,
            (_, _) => new(ClothClearanceResult.Pending), out _));
        var tested = new List<Vector3>();
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Origin, Target, Ridge, .501,
            (a, _) => { tested.Add(a); return new(ClothClearanceResult.Clear); }, out _));
        Assert.Equal(Ridge[..3], tested);
    }

    [Fact]
    public void WaitingWithoutAnyClearEvidenceDoesNotStartTheClearPrefixLifetime()
    {
        var trace = new ClothClearanceTrace();
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Origin, Target, Ridge, 0,
            (_, _) => new(ClothClearanceResult.Pending), out _));
        Prime(trace, .4);
        var tested = new List<Vector3>();
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Origin, Target, Ridge, .6,
            (a, _) => { tested.Add(a); return new(ClothClearanceResult.Clear); }, out _));
        Assert.Equal(Ridge[1..3], tested);
    }

    [Fact]
    public void ClockReversalAndExplicitResetDiscardOldPrefixes()
    {
        var trace = new ClothClearanceTrace(); Prime(trace, 2);
        AssertStartsAtOrigin(trace, 1);
        Prime(trace, 2); trace.Reset();
        AssertStartsAtOrigin(trace, 2.1);
    }

    [Theory]
    [InlineData(ClothClearanceResult.Unknown)]
    [InlineData(ClothClearanceResult.Blocked)]
    public void UncertainTerrainAndWallHitsNeverBecomeClearOrRetained(ClothClearanceResult verdict)
    {
        var trace = new ClothClearanceTrace(); Prime(trace);
        var result = trace.Query(1, Origin, Target, Ridge, .1,
            (a, b) => new(verdict, (a + b) / 2), out _);
        Assert.Equal(verdict, result); Assert.Equal(0, trace.PendingCount);
        AssertStartsAtOrigin(trace, .2);
    }

    [Fact]
    public void MalformedBlockingHitAndThrowingAdapterCannotAuthorizeOrRetainPrefix()
    {
        var trace = new ClothClearanceTrace(); Prime(trace);
        Assert.Equal(ClothClearanceResult.Unknown, trace.Query(1, Origin, Target, Ridge, .1,
            (_, _) => new(ClothClearanceResult.Blocked, new(999,0,999)), out _));
        Assert.Equal(0, trace.PendingCount);
        Prime(trace, .2);
        Assert.Throws<InvalidOperationException>(() => trace.Query(1, Origin, Target, Ridge, .3,
            (_, _) => throw new InvalidOperationException(), out _));
        Assert.Equal(0, trace.PendingCount);
    }

    [Fact]
    public void PathAndCacheCapsAreStrictAndExpiredAbandonedWorkReleasesCapacity()
    {
        var trace = new ClothClearanceTrace(); var calls = 0;
        ClothClearanceCast Wait(Vector3 a, Vector3 b) { calls++; return new(ClothClearanceResult.Pending); }
        var oversized = Enumerable.Range(0, ClothClearanceTrace.MaximumPoints + 1).Select(i => new Vector3(i,.18f,0)).ToArray();
        Assert.Equal(ClothClearanceResult.Unknown, trace.Query(1, Origin, new(oversized[^1].X,0,0), oversized, 0, Wait, out _));
        Assert.Equal(0, calls);
        for (var generation = 0; generation < ClothClearanceTrace.MaximumEntries; generation++)
            Assert.Equal(ClothClearanceResult.Pending, trace.Query(generation, Origin, Target, Ridge, 0, Wait, out _));
        Assert.Equal(ClothClearanceTrace.MaximumEntries, trace.PendingCount);
        Assert.Equal(ClothClearanceResult.Unknown, trace.Query(ClothClearanceTrace.MaximumEntries, Origin, Target, Ridge, 0, Wait, out _));
        Assert.Equal(ClothClearanceTrace.MaximumEntries, calls);
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(ClothClearanceTrace.MaximumEntries, Origin, Target, Ridge, .501, Wait, out _));
        Assert.Equal(1, trace.PendingCount);
    }

    [Fact]
    public void BadPathEndpointsAndNonfiniteGeometryInvalidatePendingWork()
    {
        var trace = new ClothClearanceTrace(); Prime(trace);
        var wrong = Ridge.ToArray(); wrong[^1].X += 1;
        Assert.Equal(ClothClearanceResult.Unknown, trace.Query(1, Origin, Target, wrong, .1, Fail, out _));
        Assert.Equal(0, trace.PendingCount);
        wrong = Ridge.ToArray(); wrong[1].Y = float.NaN;
        Assert.Equal(ClothClearanceResult.Unknown, trace.Query(1, Origin, Target, wrong, .1, Fail, out _));
        Prime(trace, .2);
        Assert.Equal(ClothClearanceResult.Unknown, trace.Query(1, Origin, Target, Ridge, double.NaN, Fail, out _));
        Assert.Equal(0, trace.PendingCount);
        static ClothClearanceCast Fail(Vector3 a, Vector3 b) => throw new Exception("Invalid input must not cast.");
    }

    private static void Prime(ClothClearanceTrace trace, double time = 0)
    {
        var allowance = 1;
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Origin, Target, Ridge, time,
            (_, _) => new(allowance-- > 0 ? ClothClearanceResult.Clear : ClothClearanceResult.Pending), out _));
    }

    private static void AssertStartsAtOrigin(ClothClearanceTrace trace, double time)
    {
        Vector3? first = null;
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Origin, Target, Ridge, time,
            (a, _) => { first ??= a; return new(ClothClearanceResult.Clear); }, out _));
        Assert.Equal(Ridge[0], first);
    }
}
