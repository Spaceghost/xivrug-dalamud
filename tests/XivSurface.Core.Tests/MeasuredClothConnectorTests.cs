using System.Numerics;
using System.Reflection;

namespace XivSurface.Core.Tests;

public sealed class MeasuredClothConnectorTests
{
    private static LayerTriangle Face(Vector3 a,Vector3 b,Vector3 c)
    {var n=Vector3.Normalize(Vector3.Cross(b-a,c-a));if(n.Y<0)n=-n;return new(a,b,c,n);}
    private static readonly LayerTriangle Floor=Face(new(-1,0,-1),new(0,0,-1),new(0,0,1));
    private static readonly LayerTriangle BevelA=Face(new(0,0,-1),new(.1f,.3f,-1),new(0,0,1));
    private static readonly LayerTriangle BevelB=Face(new(.1f,.3f,-1),new(.1f,.3f,1),new(0,0,1));
    private static readonly LayerTriangle Upper=Face(new(.1f,.3f,-1),new(1,.3f,-1),new(.1f,.3f,1));
    private static readonly LayerFloorHit Root=new(new(-.3f,0,0),Floor);

    private static LocalFloorLayer New(LayerSupportScope scope=LayerSupportScope.MeasuredCloth)
    {var layer=new LocalFloorLayer(scope);Assert.True(layer.Seed(Root));Assert.True(layer.BeginFrame(Root,0));return layer;}
    private static bool Add(LocalFloorLayer layer,Vector3 point,LayerTriangle face)
    {
        Assert.True(layer.TryProbe(new(point.X,point.Z),out var probe));
        return face.Walkable?layer.Accept(probe,new(point,face)):layer.AcceptMeasuredClothConnector(probe,new(point,face));
    }
    private static LocalFloorLayer Complete()
    {
        var layer=New();Assert.True(Add(layer,new(.025f,.075f,0),BevelA));
        Assert.True(Add(layer,new(.075f,.225f,0),BevelB));Assert.True(Add(layer,new(.2f,.3f,0),Upper));return layer;
    }

    [Fact] public void DefaultPlayerAndOrdinaryAdmissionRemainWalkableOnly()
    {
        Assert.False(BevelA.Walkable);Assert.False(new LayerFloorHit(new(.025f,.075f,0),BevelA).Valid);
        var layer=New(LayerSupportScope.WalkableOnly);
        Assert.False(Add(layer,new(.025f,.075f,0),BevelA));
        Assert.True(layer.TryProbe(new(.025f,0),out var probe));
        Assert.False(layer.Accept(probe,new(new(.025f,.075f,0),BevelA)));
        Assert.False(New().Seed(new(new(.025f,.075f,0),BevelA)));
        Assert.False(New().BeginFrame(new(new(.025f,.075f,0),BevelA),.01));
    }

    [Fact] public void ExistingClrSignaturesAndDefaultReplayStillExist()
    {
        Assert.NotNull(typeof(LocalFloorLayer).GetConstructor(Type.EmptyTypes));
        Assert.NotNull(typeof(ClothCellCeiling).GetMethod("Measure",BindingFlags.Public|BindingFlags.Instance));
        var layer=new LocalFloorLayer();Assert.True(layer.Seed(Root));Assert.True(layer.BeginFrame(Root,0));
        var state=layer.CaptureReplay();
        var restore=typeof(LocalFloorLayer).GetMethod("RestoreReplay",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.IsType<LocalFloorLayer>(restore.Invoke(null,[state]));
    }

    [Fact] public void FiniteBevelUsesActualPositiveSpanAndAllPathBreakpoints()
    {
        var layer=Complete();
        Assert.Equal(LayerQueryResult.Success,layer.TrySurfacePath(Root.Position,new(.2f,.3f,0),out var path));
        Assert.Contains(path,p=>Math.Abs(p.X)<1e-6&&Math.Abs(p.Y)<1e-6);
        Assert.Contains(path,p=>Math.Abs(p.X-.05f)<1e-6&&Math.Abs(p.Y-.15f)<1e-6);
        Assert.Contains(path,p=>Math.Abs(p.X-.1f)<1e-6&&Math.Abs(p.Y-.3f)<1e-6);
        Assert.All(path.Zip(path.Skip(1)),p=>Assert.True(p.Second.X>p.First.X));
        // Success still has its original geometry-only semantics: the caller
        // must query above EVERY segment, not the single endpoint chord.
        Assert.True(path.Length>=5);
    }

    [Fact] public void FullCellIncludesActualSteepHeightEnvelope()
    {
        var layer=Complete();var center=new LayerFloorHit(new(.05f,.15f,0),BevelA);
        Assert.False(center.Valid);
        Assert.Equal(LayerQueryResult.Success,layer.TryCellCeiling(center,
            new(-.05f,0,-.25f),new(.15f,.3f,-.25f),new(-.05f,0,.25f),new(.15f,.3f,.25f),out var ceiling));
        Assert.InRange(ceiling,.3f,.30001f);Assert.InRange(layer.CellCeilingLift,.07499f,.07501f);
        // Ordinary public helper does not reinterpret explicit role evidence.
        Assert.Equal(LayerQueryResult.Unknown,new ClothCellCeiling().Measure(center,
            new(-.05f,0,-.25f),new(.15f,.3f,-.25f),new(-.05f,0,.25f),new(.15f,.3f,.25f),
            new[]{Floor,BevelA,BevelB,Upper},out _));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void MissingConnectorNeverBridgesPathOrCell(bool one)
    {
        var layer=New();if(one)Assert.True(Add(layer,new(.025f,.075f,0),BevelA));
        Assert.Equal(LayerQueryResult.Unknown,layer.TrySurfacePath(Root.Position,new(.2f,.3f,0),out var points));Assert.Empty(points);
        Assert.Equal(LayerQueryResult.Unknown,layer.TryCellCeiling(new(new(-.025f,0,0),Floor),
            new(-.05f,0,-.25f),new(.15f,.3f,-.25f),new(-.05f,0,.25f),new(.15f,.3f,.25f),out _));
    }

    [Fact] public void UnsharedGapAndDownwardAndVerticalFacesRefuse()
    {
        var layer=New();Assert.True(layer.TryProbe(new(.025f,0),out var probe));
        Assert.False(layer.AcceptMeasuredClothConnector(probe,new(new(.025f,.075f,0),BevelA with{Normal=-BevelA.Normal})));
        var vertical=new LayerTriangle(new(0,0,-1),new(0,.3f,-1),new(0,0,1),Vector3.UnitX);
        Assert.False(layer.AcceptMeasuredClothConnector(probe,new(new(0,.075f,0),vertical)));
        var shifted=BevelA with{A=BevelA.A+Vector3.UnitX*.01f,B=BevelA.B+Vector3.UnitX*.01f,C=BevelA.C+Vector3.UnitX*.01f};
        Assert.False(layer.AcceptMeasuredClothConnector(probe,new(new(.025f,.045f,0),shifted)));
    }

    [Fact] public void RaisedProbeBandCannotAdmitConnector()
    {
        var layer=New();Assert.True(layer.TryProbe(new(.025f,0),out var probe));
        var raised=probe with{StartY=probe.StartY+1,MaximumY=probe.MaximumY+1,Length=probe.Length+1};
        Assert.False(layer.AcceptMeasuredClothConnector(raised,new(new(.025f,.075f,0),BevelA)));
    }

    [Fact] public void NarrowRetryBandAllowedButExtendedRayRefused()
    {
        var layer=New();Assert.True(layer.TryProbe(new(.025f,0),out var original));
        var narrow=original with{StartY=.2f,MaximumY=.2f,Length=1.2f};
        Assert.True(layer.AcceptMeasuredClothConnector(narrow,new(new(.025f,.075f,0),BevelA)));
        var fresh=New();
        Assert.False(fresh.AcceptMeasuredClothConnector(narrow with{Length=1.3f},new(new(.025f,.075f,0),BevelA)));
        Assert.False(fresh.AcceptMeasuredClothConnector(original with{MinimumY=-2,Length=2.35f},new(new(.025f,.075f,0),BevelA)));
    }

    [Fact] public void RawValidityDoesNotGrantAdmissionOrPlaneAccess()
    {
        var hit=new LayerFloorHit(new(.025f,.075f,0),BevelA);var layer=New();
        Assert.True(layer.IsMeasuredSupport(hit));Assert.False(New(LayerSupportScope.WalkableOnly).IsMeasuredSupport(hit));
        Assert.False(layer.Contains(BevelA));Assert.False(layer.TryMeasuredHeight(BevelA,new(.025f,0),out _));
        Assert.True(Add(layer,hit.Position,hit.Triangle));
        Assert.True(layer.TryMeasuredHeight(BevelA,new(.025f,0),out var y));Assert.InRange(y,.07499f,.07501f);
        layer.BeginFrame(Root,2.00001);
        Assert.False(layer.TryMeasuredHeight(BevelA,new(.025f,0),out _));
    }

    [Fact] public void PositiveSpanBevelCannotUseVerticalCurbClearanceException()
    {
        var layer=Complete();var upper=new Vector3(.1f,.3f,0);var lower=new Vector3(0,0,0);
        Assert.False(layer.IsVerifiedCurbTopCrossing(lower+Vector3.UnitY*.18f,upper+Vector3.UnitY*.18f,upper,Upper));
        Assert.False(layer.IsVerifiedCurb(BevelA));Assert.False(layer.IsVerifiedCurb(BevelB));
    }

    [Fact] public void ConnectorCannotGainWalkableCurbClearanceAuthority()
    {
        var layer=New();Assert.True(Add(layer,new(.025f,.075f,0),BevelA));
        Assert.True(Add(layer,new(.075f,.225f,0),BevelB));
        var upper=Upper with{A=Upper.A+Vector3.UnitY*.2f,B=Upper.B+Vector3.UnitY*.2f,C=Upper.C+Vector3.UnitY*.2f};
        LayerTriangle[] risers=[new(new(.1f,.3f,-1),new(.1f,.5f,-1),new(.1f,.3f,1),Vector3.UnitX),
            new(new(.1f,.5f,-1),new(.1f,.5f,1),new(.1f,.3f,1),Vector3.UnitX)];
        Assert.True(layer.TryProbe(new(.2f,0),out var probe));
        Assert.False(layer.Accept(probe,new(new(.2f,.5f,0),upper),risers));
        Assert.False(layer.IsVerifiedCurb(risers[0]));Assert.False(layer.IsVerifiedCurb(risers[1]));
    }

    [Fact] public void OriginalWalkableCurbRemainsValidInsideOptInScope()
    {
        var layer=New();var upper=Face(new(0,.2f,-1),new(1,.2f,-1),new(0,.2f,1));
        LayerTriangle[] risers=[new(new(0,0,-1),new(0,.2f,-1),new(0,0,1),Vector3.UnitX),
            new(new(0,.2f,-1),new(0,.2f,1),new(0,0,1),Vector3.UnitX)];
        Assert.True(layer.TryProbe(new(.2f,0),out var probe));
        Assert.True(layer.Accept(probe,new(new(.2f,.2f,0),upper),risers));
        Assert.True(layer.IsVerifiedCurb(risers[0]));Assert.True(layer.IsVerifiedCurb(risers[1]));
        Assert.True(layer.IsVerifiedCurbTopCrossing(new(0,.18f,0),new(0,.38f,0),new(0,.2f,0),upper));
    }

    private static LayerTriangle[] SlantedRisers()=>[
        Face(new(0,0,-1),new(.01f,.2f,-1),new(0,0,1)),
        Face(new(.01f,.2f,-1),new(.01f,.2f,1),new(0,0,1))];

    [Fact] public void CurbCannotLaterBecomeClothConnector()
    {
        var layer=New();var risers=SlantedRisers();
        var upper=Face(new(.01f,.2f,-1),new(1,.2f,-1),new(.01f,.2f,1));
        Assert.True(layer.TryProbe(new(.2f,0),out var probe));
        Assert.True(layer.Accept(probe,new(new(.2f,.2f,0),upper),risers));
        Assert.True(layer.IsVerifiedCurb(risers[0]));
        Assert.True(layer.TryProbe(new(.0025f,0),out var connectorProbe));
        Assert.True(layer.IsMeasuredSupport(new(new(.0025f,.05f,0),risers[0])));
        Assert.False(layer.AcceptMeasuredClothConnector(connectorProbe,new(new(.0025f,.05f,0),risers[0])));
        Assert.False(layer.Contains(risers[0]));Assert.True(layer.IsVerifiedCurb(risers[0]));
    }

    [Fact] public void ConnectorCannotLaterBeReusedAsRiserFromWalkableWitness()
    {
        var layer=New();var risers=SlantedRisers();
        Assert.True(Add(layer,new(.0025f,.05f,0),risers[0]));
        // This ray exits the original floor through another edge, so the
        // locally reached witness is still walkable. A distant shared riser
        // chain must not reuse the separately registered connector role.
        var upper=Face(new(.01f,.2f,-1),new(.01f,.2f,1),new(-.8f,.2f,3));
        var hit=new LayerFloorHit(new(-.3f,.2f,.8f),upper);Assert.True(hit.Valid);
        Assert.True(layer.TryProbe(new(-.3f,.8f),out var probe));
        Assert.True(layer.TryNearest(probe.Position,out var witness,out _));Assert.Equal(Floor,witness);
        Assert.False(layer.Accept(probe,hit,risers));
        Assert.True(layer.Contains(risers[0]));Assert.False(layer.IsVerifiedCurb(risers[0]));
    }

    [Fact] public void InvalidOrUnmeasuredPositionsDoNotCreateRole()
    {
        var layer=New();Assert.True(layer.TryProbe(new(.025f,0),out var probe));
        Assert.False(layer.AcceptMeasuredClothConnector(probe,new(new(float.NaN,.075f,0),BevelA)));
        Assert.False(layer.AcceptMeasuredClothConnector(probe,new(new(.025f,1,0),BevelA)));
        Assert.False(layer.AcceptMeasuredClothConnector(probe,new(new(.025f,.3f,0),BevelA)));
        Assert.Equal(1,layer.Count);
    }

    [Fact] public void OriginalTtlAndDeadlineStillRefuseStaleOrPartialGeometry()
    {
        var layer=Complete();Assert.Equal(LayerQueryResult.Pending,layer.TrySurfacePath(Root.Position,new(.2f,.3f,0),out var path,()=>false));Assert.Empty(path);
        Assert.False(layer.BeginFrame(Root,2.00001));
        Assert.Equal(LayerQueryResult.Unknown,layer.TrySurfacePath(Root.Position,new(.2f,.3f,0),out path));Assert.Empty(path);
    }

    [Fact] public void ConnectorEvidenceCannotRestoreAsNormalWalkableReplay()
    {
        // Explicit v2 is supported; its cloth roles may never be silently
        // reinterpreted by the unchanged v1 walkable schema.
        var state=Complete().CaptureReplay() with {Version=1,Scope=null};
        var restore=typeof(LocalFloorLayer).GetMethod("RestoreReplay",BindingFlags.NonPublic|BindingFlags.Static)!;
        var error=Assert.Throws<TargetInvocationException>(()=>restore.Invoke(null,[state]));
        Assert.IsType<ArgumentException>(error.InnerException);
    }
}
