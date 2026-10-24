using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class RaisedObstacleTests
{
    private static LayerTriangle Face(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b-a,c-a));
        return new(a,b,c,n.Y < 0 ? -n : n);
    }
    private static readonly LayerTriangle Root = Face(new(-51.638954f,42,-124.743866f),
        new(-46.665577f,42,-117.029526f),new(-48.352978f,42,-125.31035f));
    private static readonly LayerTriangle Floor = Face(new(-46.665577f,42,-117.029526f),
        new(-46.307465f,41.99811f,-117.16119f),new(-48.352978f,42,-125.31035f));
    private static readonly LayerTriangle Bevel = Face(new(-46.783546f,42.152027f,-117.9605f),
        new(-46.63384f,42.139164f,-116.833725f),new(-46.73962f,42.189568f,-117.9605f));
    private static readonly Vector3 From = new(-47.24528f,42.18f,-118.725464f);
    private static readonly Vector3 To = new(-46.4f,42.178528f,-117.200005f);
    private static readonly LayerFloorHit Hit = new(new(-46.726944f,42.179096f,-117.79003f),Bevel);
    private static LocalFloorLayer CapturedLayer()
    {
        var layer = new LocalFloorLayer(LayerSupportScope.MeasuredCloth);
        layer.BeginFrame(new(From-Vector3.UnitY*.18f,Root),1);
        Assert.True(layer.TryRetainMeasuredFloorHit(new((Floor.A+Floor.B+Floor.C)/3,Floor)));
        return layer;
    }

    [Fact]
    public void CapturedPlanterBevelBlocksWithoutReplacingTheLowerFloor()
    {
        var layer = CapturedLayer(); var before = layer.CaptureReplay();
        Assert.True(Hit.Valid); Assert.True(Bevel.Walkable);
        Assert.False(layer.TryRetainMeasuredFloorHit(Hit));
        Assert.True(layer.IsRaisedObstacle(From,To,Hit));
        var after = layer.CaptureReplay();
        Assert.Equal(before.Faces,after.Faces); Assert.Equal(before.Portals,after.Portals);
        Assert.False(layer.Contains(Bevel));
        var at = new Vector2(Hit.Position.X,Hit.Position.Z);
        Assert.True(Floor.TryHeight(at,out var y));
        Assert.Equal(LayerQueryResult.Success,
            layer.TrySurfacePath(From-Vector3.UnitY*.18f,new(at.X,y,at.Y),out _));
    }

    [Fact]
    public void ARealAdjacentRampRemainsFloorRatherThanAnObstacle()
    {
        var lower = Face(new(-2,0,-1),new(0,0,-1),new(0,0,1));
        var ramp = Face(new(0,0,-1),new(.4f,.2f,0),new(0,0,1));
        var layer = new LocalFloorLayer();layer.Seed(new(new(-.2f,0,0),lower));
        var hit = new LayerFloorHit(new(.2f,.1f,0),ramp);
        Assert.False(layer.IsRaisedObstacle(new(-.2f,.1f,0),new(.3f,.1f,0),hit));
        Assert.True(layer.TryRetainMeasuredFloorHit(hit));
        Assert.False(layer.IsRaisedObstacle(new(-.2f,.1f,0),new(.3f,.1f,0),hit));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MissingInvalidOrUnexecutedHitCannotStandInForABlocker(int bad)
    {
        var layer = CapturedLayer(); var hit = Hit; var from = From; var to = To;
        if(bad==0)hit=default;
        if(bad==1)hit=hit with { Position = hit.Position+Vector3.UnitY*.1f };
        if(bad==2)from=to;
        if(bad==3)to=from+(to-from)*.1f;
        Assert.False(layer.IsRaisedObstacle(from,to,hit));
    }

    [Fact]
    public void DeadlineAndEmptyLayerCannotClassifyTheRaisedFace()
    {
        Assert.False(CapturedLayer().IsRaisedObstacle(From,To,Hit,()=>false));
        Assert.False(new LocalFloorLayer().IsRaisedObstacle(From,To,Hit));
    }

    [Fact]
    public void CoplanarHitDoesNotBecomeAnObstacle()
    {
        var layer=CapturedLayer(); var at=new Vector2(Hit.Position.X,Hit.Position.Z);
        Floor.TryHeight(at,out var y);var point=new Vector3(at.X,y,at.Y);
        Assert.False(layer.IsRaisedObstacle(point-Vector3.UnitX,point+Vector3.UnitX,new(point,Floor)));
    }
}
