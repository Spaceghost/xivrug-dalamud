using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class LayerMaintenanceWarmupTests
{
    private static readonly LayerTriangle Left = new(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1), Vector3.UnitY);
    private static readonly LayerTriangle Middle = new(new(0, 0, -1), new(2, 0, 1), new(0, 0, 1), Vector3.UnitY);
    private static readonly LayerTriangle Right = new(new(0, 0, -1), new(2, 0, -1), new(2, 0, 1), Vector3.UnitY);
    private static readonly LayerFloorHit Root = new(new(-.2f, 0, 0), Left);

    [Fact]
    public void RuntimePreparationCannotSupplyOrRenewLiveCollisionEvidence()
    {
        var empty = new LocalFloorLayer(LayerSupportScope.MeasuredCloth);
        var live = new LocalFloorLayer(LayerSupportScope.MeasuredCloth);
        Assert.True(live.Seed(Root));
        live.BeginFrame(new(new(.2f, 0, 0), Middle), .1);
        live.BeginFrame(Root, .2);
        Assert.Equal(2, live.Count);

        LocalFloorLayer.PrepareMaintenance();
        LocalFloorLayer.PrepareMaintenance();

        Assert.Equal(0, empty.Count);
        Assert.False(empty.TryProbe(new(-.2f, 0), out _));
        Assert.Equal(2, live.Count);
        Assert.True(live.TryProbe(new(.2f, 0), out _));
        Assert.False(live.BeginFrame(Root, 2.2));
        Assert.Equal(1, live.Count);
        Assert.False(live.Contains(Middle));
    }

    [Fact]
    public void RetirementRemovesFreshFacesDisconnectedByAnExpiredPortal()
    {
        LocalFloorLayer.PrepareMaintenance();
        var live = new LocalFloorLayer(LayerSupportScope.MeasuredCloth);
        Assert.True(live.Seed(Root));
        live.BeginFrame(new(new(.2f, 0, 0), Middle), .1);
        live.BeginFrame(new(new(1.5f, 0, 0), Right), .2);
        live.BeginFrame(Root, .3);
        Assert.Equal(3, live.Count);
        // Middle is expired; Right is still fresh but cannot remain reached.
        Assert.False(live.BeginFrame(Root, 2.15));
        Assert.Equal(1, live.Count);
        Assert.True(live.Contains(Left));
        Assert.False(live.Contains(Middle));
        Assert.False(live.Contains(Right));
        Assert.False(live.TryProbe(new(1.5f, 0), out _));
    }
}
