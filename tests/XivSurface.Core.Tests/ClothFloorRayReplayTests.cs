using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFloorRayReplayTests
{
    private static ClothFloorRayReplay Ray(ClothFloorRayContext context=ClothFloorRayContext.Direct)
    {
        var face=new LayerTriangle(new(-1,0,-1),new(1,0,-1),new(0,0,1),Vector3.UnitY);
        return new(129,4,1,100,100.001,2,context,context==ClothFloorRayContext.Direct?null:new Vector2(.1f,.2f),
            new(Vector2.Zero,.35f,1.35f,-1,.35f),true,Vector3.Zero,Vector3.UnitY,face,ClothFloorRayOutcome.Accepted);
    }
    private static ClothCollisionReport Report(params ClothFloorRayReplay[] rays)
        => new(1,129,0,2,"window ended",null,null) { RawFloorRays=rays.ToImmutableArray() };

    [Fact] public void LegacyV1WithoutNewFieldStillDecodes()
    {
        var report=ClothCollisionReportFile.Decode(Encoding.UTF8.GetBytes("""
            {"Version":1,"Zone":129,"RequestedAt":0,"CompletedAt":1,"Completion":"old","PathUnknown":null,"CellPending":null}
            """));
        Assert.Empty(report.RawFloorRays);
    }
    [Fact] public void NewReceiptRoundTripPreservesExactNativeDataAndIndependentClocks()
    {
        var ray=Ray(ClothFloorRayContext.PathMissingWitness);
        var report=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(Report(ray)));
        Assert.Equal(ray,Assert.Single(report.RawFloorRays));
        Assert.NotEqual(ray.RequestedTarget,ray.Probe.Position); // honest frontier call context
        Assert.True(ray.NativeStartedSeconds>report.CompletedAt); // distinct clock domains
    }
    [Theory] [InlineData(.2838f,ClothFloorRayOutcome.SteepNormal)] [InlineData(.5f,ClothFloorRayOutcome.Accepted)]
    public void SteepReceiptPreservesExistingFloorBoundary(float normalY,ClothFloorRayOutcome expected)
    {
        var ray=Ray(); var normal=new Vector3(MathF.Sqrt(1-normalY*normalY),normalY,0);
        Assert.Equal(expected,ClothFloorRayReplay.Describe(ray.Probe,true,ray.HitPoint,normal,true));
        Assert.Equal(expected==ClothFloorRayOutcome.Accepted,ClothFloorQueryPolicy.Accept(ray.Probe,ray.HitPoint,normal,out _));
    }
    [Fact] public void MissBandNormalAndTriangleReasonsDoNotInventFloor()
    {
        var ray=Ray();
        Assert.Equal(ClothFloorRayOutcome.Miss,ClothFloorRayReplay.Describe(ray.Probe,false,default,default,false));
        Assert.Equal(ClothFloorRayOutcome.OutsideProbe,ClothFloorRayReplay.Describe(ray.Probe,true,new(0,1,0),Vector3.UnitY,false));
        Assert.Equal(ClothFloorRayOutcome.InvalidNormal,ClothFloorRayReplay.Describe(ray.Probe,true,default,default,false));
        Assert.Equal(ClothFloorRayOutcome.InvalidFloorTriangle,ClothFloorRayReplay.Describe(ray.Probe,true,default,Vector3.UnitY,false));
    }
    [Fact] public void FixedOrdinaryCapacityCannotConsumeWitnessReservation()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        for(var i=0;i<100;i++)capture.RecordFloorRay(Ray());
        Assert.False(capture.WantsFloorRay(ClothFloorRayContext.Direct));
        Assert.True(capture.WantsFloorRay(ClothFloorRayContext.PathMissingWitness));
        for(var i=0;i<100;i++)capture.RecordFloorRay(Ray(ClothFloorRayContext.PathMissingWitness));
        // Still observe into the bounded replacement ring until the captured
        // target (not just some unrelated witness) has an actual receipt.
        Assert.True(capture.WantsFloorRay(ClothFloorRayContext.PathMissingWitness));
        Assert.True(capture.TryFinish(129,10,out var report));
        Assert.Equal(40,report!.RawFloorRays.Length);
        Assert.Equal(8,report.RawFloorRays.Count(r=>r.IsWitness));
        Assert.Equal(32,report.RawFloorRays.Count(r=>!r.IsWitness));
    }
    [Fact] public void PerReceiptCopyIsAllocationFreeAndCompletedReportOwnsValues()
    {
        var capture=new ClothCollisionCapture(); var ray=Ray(); capture.Begin(129,0);
        capture.RecordFloorRay(ray); // warm method
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<30;i++)capture.RecordFloorRay(ray);
        Assert.Equal(before,GC.GetAllocatedBytesForCurrentThread());
        Assert.True(capture.TryFinish(129,10,out var report));
        capture.Begin(129,11); capture.RecordFloorRay(ray with {QueryTime=12}); capture.Cancel();
        Assert.Equal(31,report!.RawFloorRays.Length); Assert.All(report.RawFloorRays,r=>Assert.Equal(ray,r));
    }
    [Fact] public void CancelAndWrongZoneCannotLeakPreviousReceipts()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        capture.RecordFloorRay(Ray() with {Zone=130}); capture.Cancel(); capture.Begin(129,0);
        Assert.True(capture.TryFinish(130,1,out var report)); Assert.Empty(report!.RawFloorRays);
    }
    [Fact] public void CapturedWitnessWaitsForActualLaterRayButNeverBeyondTenSeconds()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        var face=Ray().Triangle;
        var layer=new LocalFloorLayer(); layer.Seed(new(Vector3.Zero,face));
        var state=layer.CaptureReplay();
        var path=new ClothPathReplay(state,Vector3.Zero,new(3,0,0),LayerQueryResult.Unknown,"gap",new(.1f,.2f));
        var cell=new ClothCellReplay(state,new(Vector3.Zero,face),default,default,default,default,LayerQueryResult.Pending,"deadline",null);
        capture.RecordPath(path); capture.RecordCell(cell);
        Assert.False(capture.TryFinish(129,1,out _));
        capture.RecordFloorRay(Ray()); Assert.False(capture.TryFinish(129,2,out _));
        capture.RecordFloorRay(Ray(ClothFloorRayContext.PathMissingWitness));
        Assert.True(capture.TryFinish(129,3,out var completed)); Assert.Equal("both failures captured",completed!.Completion);
        capture.Begin(129,0); capture.RecordPath(path); capture.RecordCell(cell);
        Assert.True(capture.TryFinish(129,10,out var timedOut)); Assert.Contains("witness ray was not observed",timedOut!.Completion);
    }
    [Fact] public void UnrelatedFullWitnessBufferCannotFinishOrExcludeCapturedTarget()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        var face=Ray().Triangle; var layer=new LocalFloorLayer(); layer.Seed(new(Vector3.Zero,face));
        var state=layer.CaptureReplay();
        for(var i=0;i<8;i++)capture.RecordFloorRay(Ray(ClothFloorRayContext.PathMissingWitness) with {RequestedTarget=new(9,i)});
        capture.RecordPath(new(state,Vector3.Zero,new(3,0,0),LayerQueryResult.Unknown,"gap",new(.1f,.2f)));
        capture.RecordCell(new(state,new(Vector3.Zero,face),default,default,default,default,LayerQueryResult.Pending,"deadline",null));
        Assert.False(capture.TryFinish(129,2,out _));
        Assert.True(capture.WantsFloorRay(ClothFloorRayContext.PathMissingWitness));
        var exact=Ray(ClothFloorRayContext.PathMissingWitness);
        capture.RecordFloorRay(exact);
        Assert.False(capture.WantsFloorRay(ClothFloorRayContext.PathMissingWitness));
        capture.RecordFloorRay(exact with {RequestedTarget=new(8,8)});
        Assert.True(capture.TryFinish(129,3,out var report));
        Assert.Equal(8,report!.RawFloorRays.Length); Assert.Contains(exact,report.RawFloorRays);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void EarlierTimeOrEpochCannotSatisfyLaterSameTargetSnapshot(bool earlierTime)
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        var face=Ray().Triangle; var layer=new LocalFloorLayer(); layer.Seed(new(Vector3.Zero,face));
        var state=layer.CaptureReplay() with {Now=2};
        var old=Ray(ClothFloorRayContext.PathMissingWitness) with
            {QueryTime=earlierTime?1:2,SupportEpoch=earlierTime?4:3};
        for(var i=0;i<8;i++)capture.RecordFloorRay(old);
        capture.RecordPath(new(state,Vector3.Zero,new(3,0,0),LayerQueryResult.Unknown,"gap",new(.1f,.2f)) {SupportEpoch=4});
        capture.RecordCell(new(state,new(Vector3.Zero,face),default,default,default,default,LayerQueryResult.Pending,"deadline",null) {SupportEpoch=4});
        Assert.False(capture.TryFinish(129,2,out _));
        Assert.True(capture.WantsFloorRay(ClothFloorRayContext.PathMissingWitness));
        var current=Ray(ClothFloorRayContext.PathMissingWitness) with {QueryTime=2,SupportEpoch=4};
        capture.RecordFloorRay(current);
        Assert.True(capture.TryFinish(129,3,out var report));
        Assert.Contains(current,report!.RawFloorRays);
        var restored=ClothCollisionReportFile.Decode(ClothCollisionReportFile.Encode(report));
        Assert.Equal(4,restored.PathUnknown!.SupportEpoch);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void MalformedMetadataRefuses(int fault)
    {
        var ray=Ray(); ray=fault switch
        {
            0=>ray with {NativeStartedSeconds=double.NaN},1=>ray with {NativeCompletedSeconds=99},
            2=>ray with {QueryTime=3},3=>ray with {RayOrdinal=0},4=>ray with {Zone=130},
            5=>ray with {SupportEpoch=-1},6=>ray with {RequestedTarget=Vector2.Zero},
            7=>ray with {Triangle=ray.Triangle with {A=new(float.NaN,0,0)}},
            8=>ray with {NativeHit=false},9=>ray with {Context=(ClothFloorRayContext)99},
            10=>ray with {Outcome=(ClothFloorRayOutcome)99},_=>ray with {RawNormal=new(float.PositiveInfinity,0,0)},
        };
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(Report(ray)));
    }
    [Theory] [InlineData(33,false)] [InlineData(9,true)] [InlineData(41,false)]
    public void FileReceiptAndPerClassCapsRefuse(int count,bool witness)
    {
        var ray=Ray(witness?ClothFloorRayContext.CellMissingWitness:ClothFloorRayContext.Direct);
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(Report(Enumerable.Repeat(ray,count).ToArray())));
    }
    [Fact] public void DecodeIndependentlyRejectsOversizedAndMalformedReceipts()
    {
        var json=JsonNode.Parse(ClothCollisionReportFile.Encode(Report(Ray())))!;
        var original=json["RawFloorRays"]![0]!.DeepClone();
        json["RawFloorRays"]=new JsonArray(Enumerable.Range(0,41).Select(_=>original.DeepClone()).ToArray());
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
        json["RawFloorRays"]=new JsonArray(original);
        original["RayOrdinal"]=0;
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
    [Fact] public void ObservationDoesNotIssueOrRepeatAnyQuery()
    {
        var baseline=Run(false); var observed=Run(true);
        Assert.Equal(3,baseline.Calls); Assert.Equal(baseline.Calls,observed.Calls);
        Assert.Equal(baseline.Hits,observed.Hits); Assert.Equal(3,observed.Receipts);
        static (int Calls,LayerFloorHit[] Hits,int Receipts) Run(bool armed)
        {
            var scene=new ObservedScene(); if(armed)scene.Capture.Begin(129,0);
            var discovery=new LayerFloorDiscovery();
            Assert.True(discovery.Seed(new(Vector3.Zero,Ray().Triangle)));
            Assert.True(discovery.BeginFrame(new(Vector3.Zero,Ray().Triangle),1));
            var hits=new LayerFloorHit[3];
            for(var i=0;i<3;i++)
                Assert.Equal(LayerQueryResult.Success,discovery.Query(new(.01f*i,0),scene,out hits[i]));
            scene.Capture.TryFinish(129,10,out var report);
            return(scene.Calls,hits,report?.RawFloorRays.Length??0);
        }
    }
    private sealed class ObservedScene : ILayerFloorScene
    {
        public readonly ClothCollisionCapture Capture=new();
        public int Calls;
        public bool CanQuery=>true;
        public bool TryFloor(ClothFloorProbe probe,out LayerFloorHit hit)
        {
            Calls++; hit=new(new(probe.Position.X,0,probe.Position.Y),Ray().Triangle);
            if(Capture.WantsFloorRay(ClothFloorRayContext.Direct))
                Capture.RecordFloorRay(Ray() with {Probe=probe,HitPoint=hit.Position,RayOrdinal=Calls});
            return hit.Valid;
        }
        public bool TryWall(Vector3 from,Vector3 to,out LayerTriangle triangle)
        { Calls++; triangle=default; return false; }
    }
}
