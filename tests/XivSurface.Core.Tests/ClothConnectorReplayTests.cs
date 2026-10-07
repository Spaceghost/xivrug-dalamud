using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace XivSurface.Core.Tests;

public sealed class ClothConnectorReplayTests
{
    private static LayerTriangle Face(Vector3 a, Vector3 b, Vector3 c)
    { var n = Vector3.Normalize(Vector3.Cross(b-a,c-a)); return new(a,b,c,n.Y < 0 ? -n : n); }
    private static readonly LayerTriangle Floor = Face(new(-1,0,-1),new(0,0,-1),new(0,0,1));
    private static readonly LayerTriangle BevelA = Face(new(0,0,-1),new(.1f,.3f,-1),new(0,0,1));
    private static readonly LayerTriangle BevelB = Face(new(.1f,.3f,-1),new(.1f,.3f,1),new(0,0,1));
    private static readonly LayerTriangle Upper = Face(new(.1f,.3f,-1),new(1,.3f,-1),new(.1f,.3f,1));
    private static readonly LayerFloorHit Root = new(new(-.3f,0,0),Floor);
    private static LocalFloorLayer Layer(bool complete = true, LayerSupportScope scope = LayerSupportScope.MeasuredCloth)
    {
        var layer = new LocalFloorLayer(scope); Assert.True(layer.Seed(Root)); Assert.True(layer.BeginFrame(Root,1));
        if (complete)
        {
            Add(new(.025f,.075f,0),BevelA); Add(new(.075f,.225f,0),BevelB); Add(new(.2f,.3f,0),Upper);
        }
        return layer;
        void Add(Vector3 point, LayerTriangle triangle)
        {
            Assert.True(layer.TryProbe(new(point.X,point.Z),out var probe));
            Assert.True(triangle.Walkable ? layer.Accept(probe,new(point,triangle))
                : layer.AcceptMeasuredClothConnector(probe,new(point,triangle)));
        }
    }
    private static ClothPathReplay Path(LocalFloorLayer layer)
    {
        var end = new Vector3(.2f,.3f,0); var result = layer.TrySurfacePath(Root.Position,end,out _);
        return new(layer.CaptureReplay(),Root.Position,end,result,layer.SurfacePathFailure,layer.SurfacePathMissingWitness)
            { SupportEpoch = 4 };
    }
    private static ClothCellReplay Cell(LocalFloorLayer layer)
    {
        LayerFloorHit center = new(new(.05f,.15f,0),BevelA);
        Vector3 a=new(-.05f,0,-.25f), b=new(.15f,.3f,-.25f), c=new(-.05f,0,.25f), d=new(.15f,.3f,.25f);
        var result=layer.TryCellCeiling(center,a,b,c,d,out _);
        return new(layer.CaptureReplay(),center,a,b,c,d,result,layer.CellCeilingFailure,layer.CellCeilingMissingWitness);
    }
    private static ClothCollisionReport Report(LocalFloorLayer? layer = null)
    { layer ??= Layer(); return new(2,129,1,1.1,"offline",Path(layer),Cell(layer)); }
    private static void Same(ClothReplayResult a, ClothReplayResult b)
    {
        Assert.Equal(a.Result,b.Result); Assert.Equal(a.Reason,b.Reason); Assert.Equal(a.MissingWitness,b.MissingWitness);
        Assert.Equal(a.DeadlineChecks,b.DeadlineChecks); Assert.Equal(a.Path.ToArray(),b.Path.ToArray());
        Assert.Equal(a.Ceiling,b.Ceiling); Assert.Equal(a.Lift,b.Lift);
    }
    private static void Reject(FloorReplayState state)
    {
        var query = Path(Layer()) with {Layer=state};
        Assert.Throws<ArgumentException>(()=>LocalFloorLayer.Replay(query));
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(Report() with {PathUnknown=query}));
    }

    [Fact] public void ExplicitRolesRoundTripExactSuccessfulPathAndFullCell()
    {
        var report=Report(); var state=report.PathUnknown!.Layer;
        Assert.Equal(2,state.Version); Assert.Equal(LayerSupportScope.MeasuredCloth,state.Scope);
        Assert.Equal(2,state.Faces.Count(face=>face.Role==FloorReplayFaceRole.ClothConnector));
        Assert.All(state.Faces,face=>Assert.True(face.WalkableGraph));
        var bytes=ClothCollisionReportFile.Encode(report); var restored=ClothCollisionReportFile.Decode(bytes);
        Assert.Equal(state.Faces.ToArray(),restored.PathUnknown!.Layer.Faces.ToArray());
        var path=LocalFloorLayer.Replay(restored.PathUnknown); Assert.Equal(LayerQueryResult.Success,path.Result);
        Assert.True(path.Path.Length>=5); Same(LocalFloorLayer.Replay(report.PathUnknown),path);
        var cell=LocalFloorLayer.Replay(restored.CellPending!); Assert.Equal(LayerQueryResult.Success,cell.Result);
        Same(LocalFloorLayer.Replay(report.CellPending!),cell); Assert.InRange(cell.Lift,.07499f,.07501f);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(5)] [InlineData(20)] [InlineData(500)]
    public void DeadlineAndMissingEvidenceResultsRoundTrip(int checks)
    {
        var report=Report(Layer(false)); var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
        Same(LocalFloorLayer.Replay(report.PathUnknown!,checks),LocalFloorLayer.Replay(restored.PathUnknown!,checks));
        Same(LocalFloorLayer.Replay(report.CellPending!,checks),LocalFloorLayer.Replay(restored.CellPending!,checks));
        Assert.Equal(LayerQueryResult.Unknown,LocalFloorLayer.Replay(restored.PathUnknown!).Result);
        Assert.Empty(LocalFloorLayer.Replay(restored.PathUnknown!).Path);
    }

    [Fact] public void ExpiredRecordedGeometryStaysExpiredRatherThanRetimestamping()
    {
        var path=Path(Layer()); var state=path.Layer with {Now=3.00001};
        var expired=path with {Layer=state};
        Assert.Equal(LayerQueryResult.Unknown,LocalFloorLayer.Replay(expired).Result);
        var encoded=ClothCollisionReportFile.Encode(new(2,129,3,3.1,"expired",expired,null));
        var read=ClothCollisionReportFile.Decode(encoded);
        Same(LocalFloorLayer.Replay(expired),LocalFloorLayer.Replay(read.PathUnknown!));
        Assert.All(read.PathUnknown!.Layer.Faces,face=>Assert.Equal(1,face.ObservedAt));
    }

    [Fact] public void DefaultV1SchemaOmitsTagsAndOriginalClrConstructorsRemain()
    {
        var layer=Layer(false,LayerSupportScope.WalkableOnly); var path=Path(layer);
        Assert.Equal(1,path.Layer.Version); Assert.Null(path.Layer.Scope); Assert.All(path.Layer.Faces,face=>Assert.Null(face.Role));
        var report=new ClothCollisionReport(1,129,1,1.1,"legacy",path,null);
        var bytes=ClothCollisionReportFile.Encode(report); var text=Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\"Role\"",text); Assert.DoesNotContain("\"Scope\"",text);
        Same(LocalFloorLayer.Replay(path),LocalFloorLayer.Replay(ClothCollisionReportFile.Decode(bytes).PathUnknown!));
        Assert.NotNull(typeof(FloorReplayFace).GetConstructor([typeof(LayerTriangle),typeof(double),typeof(bool)]));
        Assert.NotNull(typeof(FloorReplayState).GetConstructor([typeof(int),typeof(double),typeof(LayerTriangle),typeof(Vector3),
            typeof(ImmutableArray<FloorReplayFace>),typeof(ImmutableArray<FloorReplayPortal>)]));
        Assert.NotNull(typeof(ClothCollisionReport).GetConstructor([typeof(int),typeof(uint),typeof(double),typeof(double),
            typeof(string),typeof(ClothPathReplay),typeof(ClothCellReplay)]));
    }

    [Fact] public void LegacyOuterReportCannotMasqueradeClothStateOrRolesAsV1()
    {
        var report=Report(); Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with {Version=1}));
        var state=report.PathUnknown!.Layer;
        Reject(state with {Version=1,Scope=null});
        Reject(state with {Version=1,Scope=null,Faces=state.Faces.Select(face=>face with {Role=null}).ToImmutableArray()});
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InvalidScopeRoleAndGraphCombinationsRefuse(int kind)
    {
        var state=Path(Layer()).Layer; var face=state.Faces[1];
        if(kind==0) {Reject(state with {Scope=null});return;}
        if(kind==1) {Reject(state with {Scope=LayerSupportScope.WalkableOnly});return;}
        face=kind switch {2=>face with {Role=null},3=>face with {Role=(FloorReplayFaceRole)99},
            4=>face with {Role=FloorReplayFaceRole.WalkableFloor},_=>face with {WalkableGraph=false}};
        Reject(state with {Faces=state.Faces.SetItem(1,face)});
    }

    [Fact] public void SteepPlayerRootAndDisconnectedTaggedFacesRefuse()
    {
        var state=Path(Layer()).Layer;
        Reject(state with {Root=BevelA,RootPoint=new(.025f,.075f,0)});
        Reject(state with {Portals=[]});
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ForgedPortalEndpointsRolesAndReciprocityRefuse(int kind)
    {
        var state=Path(Layer()).Layer; var edge=state.Portals[0];
        if(kind==0) {Reject(state with {Portals=state.Portals.Add(edge)});return;}
        if(kind==1) {Reject(state with {Portals=state.Portals.RemoveAt(0)});return;}
        edge=kind switch {2=>edge with {ExitA=edge.ExitA+Vector3.UnitX},3=>edge with {From=edge.To},
            4=>edge with {Risers=[1]},_=>edge with {ExitA=edge.ExitB}};
        Reject(state with {Portals=state.Portals.SetItem(0,edge)});
    }

    [Fact] public void OriginalWalkableRiserChainRoundTripsWithExplicitRiserRole()
    {
        var lower=Face(new(-2,0,-1),new(0,0,-1),new(0,0,1));
        var upper=Face(new(0,.2f,-1),new(2,.2f,1),new(0,.2f,1));
        var risers=new[] {Face(new(0,0,-1),new(0,0,1),new(0,.2f,1)),Face(new(0,0,-1),new(0,.2f,1),new(0,.2f,-1))};
        var layer=new LocalFloorLayer(LayerSupportScope.MeasuredCloth); Vector3 from=new(-.2f,0,0),to=new(.2f,.2f,0);
        Assert.True(layer.Seed(new(from,lower))); Assert.True(layer.TryProbe(new(.2f,0),out var probe));
        Assert.True(layer.Accept(probe,new(to,upper),risers)); var result=layer.TrySurfacePath(from,to,out var points);
        var state=layer.CaptureReplay(); Assert.Equal(2,state.Faces.Count(face=>face.Role==FloorReplayFaceRole.Riser));
        var path=new ClothPathReplay(state,from,to,result,layer.SurfacePathFailure,layer.SurfacePathMissingWitness);
        var report=new ClothCollisionReport(2,129,0,1,"curb",path,null);
        var replay=LocalFloorLayer.Replay(ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report)).PathUnknown!);
        Assert.Equal(result,replay.Result); Assert.Equal(points,replay.Path.ToArray());
        var edge=state.Portals[0]; var bad=edge with {Risers=[edge.From]};
        Reject(state with {Portals=state.Portals.SetItem(0,bad)});
        Reject(state with {Portals=state.Portals.SetItem(0,edge with {Risers=[edge.Risers[0],edge.Risers[0]]})});
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidFiniteMetadataAndCapsRefuse(int kind)
    {
        var state=Path(Layer()).Layer;
        if(kind==0)Reject(state with {Now=double.NaN});
        else if(kind==1)Reject(state with {Faces=state.Faces.SetItem(0,state.Faces[0] with {ObservedAt=2})});
        else if(kind==2)Reject(state with {Faces=Enumerable.Repeat(state.Faces[0],LocalFloorLayer.MaximumReplayFaces+1).ToImmutableArray()});
        else
        {
            var report=Report(); Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with
                {PathUnknown=report.PathUnknown! with {From=new(float.NaN,0,0)}}));
        }
    }

    private static ClothFloorRayReplay ConnectorRay()=>new(129,4,1,10,10.001,1,ClothFloorRayContext.Direct,null,
        new(new(.025f,0),.35f,1.35f,-1,.35f),true,new(.025f,.075f,0),BevelA.Normal,BevelA,
        ClothFloorRayOutcome.AcceptedClothConnector);

    [Fact] public void NewReceiptIdIsAppendedAndDescribeNeverInfersAdmission()
    {
        Assert.Equal(6,(int)ClothFloorRayOutcome.InvalidFloorTriangle);
        Assert.Equal(7,(int)ClothFloorRayOutcome.AcceptedClothConnector);
        var ray=ConnectorRay(); Assert.Equal(ClothFloorRayOutcome.SteepNormal,
            ClothFloorRayReplay.Describe(ray.Probe,true,ray.HitPoint,ray.RawNormal,true));
        var report=new ClothCollisionReport(2,129,1,1.1,"receipt only",null,null){RawFloorRays=[ray]};
        Assert.Equal(ray,Assert.Single(ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report)).RawFloorRays));
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with {Version=1}));
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with {RawFloorRays=[ray with {NativeHit=false}]}));
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with {RawFloorRays=[ray with {RawNormal=-ray.RawNormal}]}));
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with {RawFloorRays=[ray with {RawNormal=Vector3.UnitY}]}));
    }

    [Fact] public void ActualZeroRawNormalReceiptUsesUpwardDerivedTriangleNormal()
    {
        var ray=ConnectorRay() with {RawNormal=Vector3.Zero};
        var report=new ClothCollisionReport(2,129,1,1.1,"derived native normal",null,null){RawFloorRays=[ray]};
        var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
        Assert.Equal(Vector3.Zero,Assert.Single(restored.RawFloorRays).RawNormal);
        Assert.Equal(ClothFloorRayOutcome.AcceptedClothConnector,restored.RawFloorRays[0].Outcome);
        Assert.Null(restored.PathUnknown); // A receipt never creates graph evidence.
    }

    [Fact] public void GeometricConnectorWithSuppliedNormalAboveWalkableThresholdRoundTrips()
    {
        var normal=Vector3.Normalize(new Vector3(-.866f,.5001f,0));
        var triangle=new LayerTriangle(new(0,0,-1),new(.1f,.17325f,-1),new(0,0,1),normal);
        Assert.True(triangle.Valid); Assert.False(triangle.Walkable); Assert.True(normal.Y>.5f);
        Assert.True(Math.Abs(Vector3.Normalize(Vector3.Cross(triangle.B-triangle.A,triangle.C-triangle.A)).Y)<.5f);
        var ray=ConnectorRay() with {HitPoint=new(.025f,.0433125f,0),RawNormal=normal,Triangle=triangle};
        var layer=Layer(false);
        Assert.True(layer.AcceptMeasuredClothConnector(ray.Probe,new(ray.HitPoint,triangle)));
        Assert.Equal(ClothFloorRayOutcome.InvalidFloorTriangle,
            ClothFloorRayReplay.Describe(ray.Probe,true,ray.HitPoint,ray.RawNormal,false));
        var report=new ClothCollisionReport(2,129,1,1.1,"accepted geometric connector",null,null){RawFloorRays=[ray]};
        var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
        Assert.Equal(ray,Assert.Single(restored.RawFloorRays));
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AcceptedConnectorReceiptStillRequiresActualProbePointAndHeightBand(int kind)
    {
        var ray=ConnectorRay();
        var probe=kind switch {0=>ray.Probe with {Position=new(.5f,0)},
            1=>ray.Probe with {MinimumY=.1f},_=>ray.Probe with {MaximumY=.05f}};
        Assert.True(probe.Valid);
        var report=new ClothCollisionReport(2,129,1,1.1,"outside probe",null,null){RawFloorRays=[ray with {Probe=probe}]};
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report));
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void RawNormalFallbackUsesExactProducerThreshold(int kind)
    {
        const float threshold=1e-10f;
        var x=1e-5f;
        var at=new Vector3(x,-MathF.Sqrt(threshold-x*x),0);
        Assert.Equal(threshold,at.LengthSquared());
        var raw=kind switch {0=>Vector3.Zero,1=>new Vector3(0,-1e-7f,0),2=>at,
            3=>new Vector3(0,-MathF.BitIncrement(x),0),_=>-BevelA.Normal};
        if(kind<=2)Assert.True(raw.LengthSquared()<=threshold);
        else Assert.True(raw.LengthSquared()>threshold);
        var ray=ConnectorRay() with {RawNormal=raw};
        var report=new ClothCollisionReport(2,129,1,1.1,"producer threshold",null,null){RawFloorRays=[ray]};
        if(kind<=2)
        {
            var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
            Assert.Equal(raw,Assert.Single(restored.RawFloorRays).RawNormal);
        }
        else Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CaptureSelectsV2FromEitherClothStateOrAcceptedReceipt(bool receiptOnly)
    {
        var capture=new ClothCollisionCapture(); Assert.True(capture.Begin(129,1));
        if(receiptOnly)capture.RecordFloorRay(ConnectorRay());
        else capture.RecordPath(Path(Layer(false)));
        Assert.True(capture.TryFinish(129,11,out var report)); Assert.Equal(2,report!.Version);
        var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
        if(receiptOnly) {Assert.Null(restored.PathUnknown);Assert.Null(restored.CellPending);Assert.Single(restored.RawFloorRays);}
        else Assert.Equal(2,restored.PathUnknown!.Layer.Version);
    }

    [Fact] public void OuterV2MayDeliberatelyContainLegacyV1Snapshot()
    {
        var path=Path(Layer(false,LayerSupportScope.WalkableOnly));
        var report=new ClothCollisionReport(2,129,1,1.1,"mixed",path,Cell(Layer()));
        var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
        Assert.Equal(1,restored.PathUnknown!.Layer.Version); Assert.Null(restored.PathUnknown.Layer.Scope);
        Assert.Equal(2,restored.CellPending!.Layer.Version);
    }
}
