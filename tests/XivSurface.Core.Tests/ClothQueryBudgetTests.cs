using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothQueryBudgetTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1.999, 0, true)]
    [InlineData(2, 0, false)]
    [InlineData(2.5, 0, false)]
    [InlineData(2.5, .5, false)]
    [InlineData(2.5, 1, true)]
    [InlineData(6.3943, 6.251, true)] // captured GC-interrupted center query
    [InlineData(7.5, 5.5, false)]
    [InlineData(7.5, 7, true)] // credit remains capped at 6, not 7
    [InlineData(7.999, 6, true)]
    [InlineData(8, 6, false)]
    [InlineData(50, 49, false)]
    [InlineData(3, 4, false)] // counter increment cannot fit this interval
    [InlineData(-1, 0, false)]
    [InlineData(1, -1, false)]
    [InlineData(double.NaN, 0, false)]
    [InlineData(3, double.NaN, false)]
    [InlineData(double.PositiveInfinity, 0, false)]
    [InlineData(3, double.PositiveInfinity, false)]
    public void WorkPauseCreditAndElapsedCeilingsRemainIndependent(double elapsed, double pause, bool allowed)
    {
        var budget = new ClothQueryBudget();
        Assert.Equal(allowed, budget.CanContinue(elapsed, pause));
        Assert.InRange(budget.PauseCreditMilliseconds, 0, ClothQueryBudget.MaxPauseCreditMilliseconds);
        Assert.Equal(allowed && elapsed >= ClothQueryBudget.WorkMilliseconds, budget.UsedPauseCredit);
    }

    [Fact]
    public void AnExpiredUpdateCannotBeRevivedByALateCounterIncrement()
    {
        var budget = new ClothQueryBudget();
        Assert.False(budget.CanContinue(2, 0));
        Assert.False(budget.CanContinue(2.5, 2));
        budget.Reset();
        Assert.True(budget.CanContinue(2.5, 2));
        Assert.True(budget.UsedPauseCredit);
        budget.Reset();
        Assert.False(budget.UsedPauseCredit);
        Assert.Equal(0, budget.PauseCreditMilliseconds);
        Assert.True(budget.CanContinue(1, 0));
    }

    [Fact]
    public void ClockAndPauseCounterRollbackFailClosed()
    {
        var budget = new ClothQueryBudget();
        Assert.True(budget.CanContinue(1.5, 0));
        Assert.False(budget.CanContinue(1, 0));
        budget.Reset();
        Assert.True(budget.CanContinue(3, 2));
        Assert.False(budget.CanContinue(3.25, 1.75));
        Assert.True(budget.Expired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseCreditNeverSubstitutesForANewActualFloorHit(bool hit)
    {
        var scene = new PausedScene(hit);
        var discovery = new LayerFloorDiscovery();
        var floor = new LayerFloorHit(Vector3.Zero, PausedScene.Triangle);
        discovery.Seed(floor);
        discovery.BeginFrame(floor, .1);
        var result = discovery.Query(new(.1f, .1f), scene, out var measured);
        Assert.True(scene.FloorCalls > 0);
        if (hit)
        {
            Assert.Equal(LayerQueryResult.Success, result);
            Assert.True(measured.Valid);
            Assert.Equal(new Vector3(.1f, 0, .1f), measured.Position);
        }
        else Assert.NotEqual(LayerQueryResult.Success, result);
    }

    private sealed class PausedScene(bool hit) : ILayerFloorScene
    {
        internal static readonly LayerTriangle Triangle = new(new(-2, 0, -2), new(2, 0, -2), new(0, 0, 2), Vector3.UnitY);
        private readonly ClothQueryBudget budget = new();
        public int FloorCalls { get; private set; }
        public bool CanQuery => budget.CanContinue(6.3943, 6.251);
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit result)
        {
            FloorCalls++;
            result = hit ? new(new(probe.Position.X, 0, probe.Position.Y), Triangle) : default;
            return hit;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle) { triangle = default; return false; }
    }
}
