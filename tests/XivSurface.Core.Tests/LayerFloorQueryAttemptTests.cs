using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class LayerFloorQueryAttemptTests
{
    private static readonly LayerTriangle Ground = new(new(-1, 0, -1), new(1, 0, -1), new(-1, 0, 1), Vector3.UnitY);
    private static readonly LayerTriangle Neighbor = new(new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1), Vector3.UnitY);
    private static readonly LayerTriangle Low = new(new(-1, 0, -1), new(0, 0, -1), new(0, 0, 1), Vector3.UnitY);
    private static readonly LayerTriangle High = new(new(0, .2f, -1), new(1, .2f, 1), new(0, .2f, 1), Vector3.UnitY);
    private enum Outcome { Hit, Miss, Defer, UncountedMiss, WrongHit }

    private sealed class Scene : ILayerFloorScene, ILayerFloorQueryAttempts
    {
        public bool CanQuery => Allowed;
        public bool Allowed = true;
        public int Raycasts { get; private set; }
        public Outcome FloorOutcome;
        public bool ExhaustOnMiss = true, ExhaustOnHit;
        public LayerTriangle Face = Ground;
        public int DeferWallCall, MissAndExhaustWallCall;
        public readonly List<ClothFloorProbe> FloorCalls = [];
        public readonly List<(Vector3 From, Vector3 To)> WallCalls = [];
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            FloorCalls.Add(probe); hit = default;
            if (FloorOutcome == Outcome.Defer) { Allowed = false; return false; }
            if (FloorOutcome == Outcome.UncountedMiss) return false;
            Raycasts++;
            if (FloorOutcome == Outcome.Miss) { if (ExhaustOnMiss) Allowed = false; return false; }
            var triangle = FloorOutcome == Outcome.WrongHit ? High : Face;
            hit = new(new(probe.Position.X, triangle.A.Y, probe.Position.Y), triangle);
            if (ExhaustOnHit) Allowed = false;
            return true;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
        {
            WallCalls.Add((from, to)); triangle = default;
            if (WallCalls.Count == DeferWallCall) { Allowed = false; return false; }
            Raycasts++;
            if (WallCalls.Count == MissAndExhaustWallCall) Allowed = false;
            return false;
        }
    }

    private sealed class Legacy(Scene inner) : ILayerFloorScene
    {
        public bool CanQuery => inner.CanQuery;
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit) => inner.TryFloor(probe, out hit);
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle) => inner.TryWall(from, to, out triangle);
    }

    private static LayerFloorDiscovery Refreshable(out Scene scene)
    {
        var layer = new LayerFloorDiscovery(); scene = new() { Face = Neighbor };
        Assert.True(layer.Seed(new(new(-.5f, 0, -.5f), Ground)));
        Assert.Equal(LayerQueryResult.Success, layer.Query(new(.5f, .5f), scene, out _));
        Assert.True(layer.BeginFrame(new(new(-.5f, 0, -.5f), Ground), 1.2));
        Assert.True(layer.Layer.TryRefresh(out var face)); Assert.Equal(Neighbor, face);
        return layer;
    }

    [Fact]
    public void ExpiryBetweenOuterCheckAndNativeAdmissionDefersRefreshWithoutRenewingEvidence()
    {
        var layer = Refreshable(out var scene); var before = scene.Raycasts;
        scene.FloorOutcome = Outcome.Defer;
        Assert.True(layer.Refresh(scene));
        Assert.Equal(before, scene.Raycasts); Assert.True(layer.Layer.Contains(Neighbor));
        Assert.True(layer.Layer.TryRefresh(out var stillDue)); Assert.Equal(Neighbor, stillDue);
        // Deferred is not refreshed: the original two-second expiry still wins.
        Assert.False(layer.BeginFrame(new(new(-.5f, 0, -.5f), Ground), 2.001));
        Assert.False(layer.Layer.Contains(Neighbor));
    }

    [Fact]
    public void RealRefreshMissThatConsumesLastBudgetStillInvalidatesWholeLayer()
    {
        var layer = Refreshable(out var scene); var before = scene.Raycasts;
        scene.FloorOutcome = Outcome.Miss;
        Assert.False(layer.Refresh(scene));
        Assert.Equal(before + 1, scene.Raycasts); Assert.False(scene.CanQuery);
        Assert.Equal(0, layer.Layer.Count);
    }

    [Fact]
    public void ActualMatchingRefreshThatFinishesAfterDeadlineStillRenewsItsEvidence()
    {
        var layer = Refreshable(out var scene); scene.ExhaustOnHit = true;
        var before = scene.Raycasts;
        Assert.True(layer.Refresh(scene)); Assert.False(scene.CanQuery);
        Assert.Equal(before + 1, scene.Raycasts);
        Assert.False(layer.Layer.TryRefresh(out _));
        Assert.True(layer.BeginFrame(new(new(-.5f, 0, -.5f), Ground), 2.001));
        Assert.True(layer.Layer.Contains(Neighbor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessfulChangedOrInvalidHitIsNeverDeferred(bool budgetRemains)
    {
        var layer = Refreshable(out var scene); scene.FloorOutcome = Outcome.WrongHit;
        scene.ExhaustOnHit = !budgetRemains;
        // A received wrong face invalidates regardless of the later budget.
        Assert.False(layer.Refresh(scene)); Assert.Equal(0, layer.Layer.Count);
        Assert.Equal(budgetRemains, scene.CanQuery);
    }

    [Fact]
    public void ProviderWithoutReceiptRetainsOriginalFailClosedRefreshBehavior()
    {
        var layer = Refreshable(out var scene); scene.FloorOutcome = Outcome.Defer;
        Assert.False(layer.Refresh(new Legacy(scene))); Assert.Equal(0, layer.Layer.Count);
    }

    [Fact]
    public void UnchangedCounterWithoutExpiredBudgetIsNotPermissionToDefer()
    {
        var layer = Refreshable(out var scene); scene.FloorOutcome = Outcome.UncountedMiss;
        Assert.False(layer.Refresh(scene)); Assert.Equal(0, layer.Layer.Count);
    }

    [Fact]
    public void DeferredDirectFloorRetainsExactlyTheUnattemptedProbeForNextUpdate()
    {
        var layer = new LayerFloorDiscovery(); Assert.True(layer.Seed(new(new(-.5f, 0, -.5f), Ground)));
        var scene = new Scene { FloorOutcome = Outcome.Defer };
        var wanted = new Vector2(-.2f, -.2f);
        Assert.Equal(LayerQueryResult.Pending, layer.Query(wanted, scene, out _));
        Assert.Equal(0, scene.Raycasts); Assert.Equal(1, layer.Pending);
        scene.Allowed = true; scene.FloorOutcome = Outcome.Hit;
        Assert.Equal(LayerQueryResult.Success, layer.Query(wanted, scene, out _));
        Assert.Equal(1, scene.Raycasts); Assert.Equal(2, scene.FloorCalls.Count);
        Assert.Equal(scene.FloorCalls[0], scene.FloorCalls[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualDirectMissIsNotRetriedAsIfUnattempted(bool receipts)
    {
        var layer = new LayerFloorDiscovery(); Assert.True(layer.Seed(new(new(-.5f, 0, -.5f), Ground)));
        var scene = new Scene { FloorOutcome = Outcome.Miss };
        ILayerFloorScene provider = receipts ? scene : new Legacy(scene);
        var wanted = new Vector2(-.2f, -.2f);
        Assert.Equal(LayerQueryResult.Pending, layer.Query(wanted, provider, out _));
        Assert.Equal(1, scene.Raycasts);
        scene.Allowed = true; scene.FloorOutcome = Outcome.Hit;
        Assert.Equal(LayerQueryResult.Unknown, layer.Query(wanted, provider, out _));
        Assert.Equal(2, scene.FloorCalls.Count);
        // The authentic miss selected a measured-boundary frontier, rather
        // than replaying the exact direct ray as if it had never been issued.
        Assert.NotEqual(scene.FloorCalls[0], scene.FloorCalls[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DeferredRiserProbeDoesNotAdvanceItsPhase(int deferCall)
    {
        var layer = new LayerFloorDiscovery(); Assert.True(layer.Seed(new(new(-.1f, 0, 0), Low)));
        var scene = new Scene { Face = High, DeferWallCall = deferCall };
        var wanted = new Vector2(.1f, 0);
        Assert.Equal(LayerQueryResult.Pending, layer.Query(wanted, scene, out _));
        Assert.Equal(deferCall, scene.WallCalls.Count);
        Assert.Equal(deferCall, scene.Raycasts); // one floor plus preceding actual wall calls
        var deferred = scene.WallCalls[^1];
        scene.Allowed = true; scene.DeferWallCall = 0;
        layer.Query(wanted, scene, out _);
        Assert.True(scene.WallCalls.Count > deferCall);
        Assert.Equal(deferred, scene.WallCalls[deferCall]);
    }

    [Fact]
    public void ActualFirstRiserMissAdvancesToSecondProbeEvenWhenItUsesLastBudget()
    {
        var layer = new LayerFloorDiscovery(); Assert.True(layer.Seed(new(new(-.1f, 0, 0), Low)));
        var scene = new Scene { Face = High, MissAndExhaustWallCall = 1 };
        var wanted = new Vector2(.1f, 0);
        Assert.Equal(LayerQueryResult.Pending, layer.Query(wanted, scene, out _));
        Assert.Equal(2, scene.Raycasts); var first = Assert.Single(scene.WallCalls);
        scene.Allowed = true; scene.MissAndExhaustWallCall = 0;
        layer.Query(wanted, scene, out _);
        Assert.True(scene.WallCalls.Count >= 2);
        Assert.NotEqual(first, scene.WallCalls[1]);
    }
}
