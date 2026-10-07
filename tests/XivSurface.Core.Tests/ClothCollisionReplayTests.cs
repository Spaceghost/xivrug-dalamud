using System.Collections.Immutable;
using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothCollisionReplayTests
{
    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b-a,c-a));
        return new(a,b,c,n.Y < 0 ? -n : n);
    }
    private static LocalFloorLayer Plane()
    {
        var layer = new LocalFloorLayer();
        Assert.True(layer.Seed(new(Vector3.Zero, Triangle(new(-10,0,-10),new(10,0,-10),new(0,0,10)))));
        return layer;
    }
    private static ClothPathReplay Path(LocalFloorLayer layer, Vector3? to = null)
    {
        var end = to ?? new Vector3(1,0,0);
        var result = layer.TrySurfacePath(Vector3.Zero,end,out _);
        return new(layer.CaptureReplay(),Vector3.Zero,end,result,layer.SurfacePathFailure,layer.SurfacePathMissingWitness);
    }
    private static ClothCellReplay Cell(LocalFloorLayer layer)
    {
        var state = layer.CaptureReplay(); var center = new LayerFloorHit(Vector3.Zero,state.Root);
        Vector3 a=new(-.1f,0,-.1f), b=new(.1f,0,-.1f), c=new(-.1f,0,.1f), d=new(.1f,0,.1f);
        var result=layer.TryCellCeiling(center,a,b,c,d,out _,()=>false);
        return new(layer.CaptureReplay(),center,a,b,c,d,result,layer.CellCeilingFailure,layer.CellCeilingMissingWitness);
    }
    private static ClothCollisionReport Report() => new(1,129,0,1,"both failures captured",Path(Plane(),new(30,0,0)),Cell(Plane()));

    [Fact] public void SamePathImplementationReplaysSuccessAndMissingWitness()
    {
        var layer=Plane(); var complete=Path(layer); var missing=Path(layer,new(30,0,0));
        Assert.Equal(LayerQueryResult.Success,LocalFloorLayer.Replay(complete).Result);
        var replay=LocalFloorLayer.Replay(missing);
        Assert.Equal(missing.Result,replay.Result); Assert.Equal(missing.Reason,replay.Reason);
        Assert.Equal(missing.MissingWitness,replay.MissingWitness); Assert.NotNull(replay.MissingWitness);
    }
    [Fact] public void SlicedCellAndUnlimitedReplayDistinguishDeadlineFromGeometry()
    {
        var cell=Cell(Plane()); Assert.Equal(LayerQueryResult.Pending,cell.Result);
        Assert.Equal(LayerQueryResult.Pending,LocalFloorLayer.Replay(cell,0).Result);
        Assert.Equal(LayerQueryResult.Success,LocalFloorLayer.Replay(cell).Result);
        Assert.Equal(LocalFloorLayer.Replay(cell,2),LocalFloorLayer.Replay(cell,2));
    }
    [Fact] public void SnapshotOwnsGeometryAndOriginalExpiryAfterLayerChanges()
    {
        var layer=Plane(); var saved=Path(layer); var before=LocalFloorLayer.Replay(saved);
        layer.Seed(default);
        Assert.Equal(before.Result,LocalFloorLayer.Replay(saved).Result);
        Assert.Single(saved.Layer.Faces); Assert.Equal(0,saved.Layer.Now);
    }
    [Fact] public void ExactRiserPortalsReplayInOriginalOrder()
    {
        var lower=Triangle(new(-2,0,-1),new(0,0,-1),new(0,0,1));
        var upper=Triangle(new(0,.2f,-1),new(2,.2f,1),new(0,.2f,1));
        var risers=new[] {Triangle(new(0,0,-1),new(0,0,1),new(0,.2f,1)),Triangle(new(0,0,-1),new(0,.2f,1),new(0,.2f,-1))};
        var layer=new LocalFloorLayer(); Vector3 from=new(-.2f,0,0),to=new(.2f,.2f,0);
        Assert.True(layer.Seed(new(from,lower))); Assert.True(layer.TryProbe(new(.2f,0),out var probe));
        Assert.True(layer.Accept(probe,new(to,upper),risers));
        var result=layer.TrySurfacePath(from,to,out var expected);
        var query=new ClothPathReplay(layer.CaptureReplay(),from,to,result,layer.SurfacePathFailure,layer.SurfacePathMissingWitness);
        Assert.NotEmpty(query.Layer.Portals); Assert.Equal(result,LocalFloorLayer.Replay(query).Result);
        Assert.Equal(expected,LocalFloorLayer.Replay(query).Path.ToArray());
    }
    [Fact] public void JsonRoundTripPreservesExactCoordinatesTimesAndResults()
    {
        var before=Report(); var bytes=ClothCollisionReportFile.Encode(before);
        var after=ClothCollisionReportFile.Decode(bytes);
        Assert.Equal(before.PathUnknown!.Layer.Faces.ToArray(),after.PathUnknown!.Layer.Faces.ToArray());
        Assert.Equal(before.PathUnknown.From,after.PathUnknown.From);
        Assert.Equal(before.CellPending!.A,after.CellPending!.A);
        Assert.Equal(LocalFloorLayer.Replay(before.PathUnknown).Result,LocalFloorLayer.Replay(after.PathUnknown).Result);
        Assert.Equal(LocalFloorLayer.Replay(before.CellPending).Result,LocalFloorLayer.Replay(after.CellPending).Result);
    }
    [Fact] public void NonfiniteRootPointCannotRestoreEvenIfContainsWouldPass()
    {
        var query=Path(Plane());
        Assert.Throws<ArgumentException>(()=>LocalFloorLayer.Replay(query with {Layer=query.Layer with {RootPoint=new(0,float.NaN,0)}}));
    }
    [Fact] public void FutureFaceTimestampAndInvalidPortalRefuse()
    {
        var query=Path(Plane());
        Assert.Throws<ArgumentException>(()=>LocalFloorLayer.Replay(query with {Layer=query.Layer with {Faces=[query.Layer.Faces[0] with {ObservedAt=1}]}}));
        var edge=new FloorReplayPortal(0,10,[],Vector3.Zero,Vector3.Zero,Vector3.Zero,Vector3.Zero);
        Assert.Throws<ArgumentException>(()=>LocalFloorLayer.Replay(query with {Layer=query.Layer with {Portals=[edge]}}));
    }
    [Fact] public void GeometryAndInputByteCapsRefuseBeforeReplay()
    {
        var report=Report(); var query=report.PathUnknown!;
        var oversized=Enumerable.Repeat(query.Layer.Faces[0],LocalFloorLayer.MaximumReplayFaces+1).ToImmutableArray();
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Encode(report with {PathUnknown=query with {Layer=query.Layer with {Faces=oversized}}}));
        Assert.Throws<InvalidDataException>(()=>ClothCollisionReportFile.Decode(new byte[ClothCollisionReportFile.MaximumBytes+1]));
    }
    [Fact] public void CaptureKeepsFirstIndependentFailuresOnly()
    {
        var capture=new ClothCollisionCapture(); Assert.True(capture.Begin(129,0)); Assert.False(capture.Begin(129,1));
        var first=Report().PathUnknown!; capture.RecordPath(first);
        capture.RecordPath(first with {Reason="later"});
        Assert.False(capture.TryFinish(129,1,out _));
        capture.RecordCell(Report().CellPending!);
        // A captured missing witness is not proof that its native ray ran.
        Assert.False(capture.TryFinish(129,2,out _));
        Assert.True(capture.TryFinish(129,10,out var report)); Assert.Same(first,report!.PathUnknown);
        Assert.False(capture.Active);
    }
    [Theory] [InlineData(129,10,"window ended")] [InlineData(130,1,"zone changed")] [InlineData(129,-1,"clock invalidated")]
    public void WindowEndsWithoutFabricatingUnobservedFailures(uint zone,double time,string expected)
    {
        var capture=new ClothCollisionCapture(); Assert.True(capture.Begin(129,0));
        Assert.True(capture.TryFinish(zone,time,out var report)); Assert.StartsWith(expected,report!.Completion);
        Assert.Null(report.PathUnknown); Assert.Null(report.CellPending); Assert.False(capture.Active);
    }
    [Fact] public void SnapshotInvariantFailureIsExplicitAndCancelLeavesNoReport()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0); capture.FailCapture();
        Assert.True(capture.TryFinish(129,1,out var report)); Assert.Contains("invariant",report!.Completion);
        capture.Begin(129,2); capture.Cancel(); Assert.False(capture.TryFinish(129,3,out _));
    }
    [Fact] public void FirstPathDiscoveryAndFinalAttemptAreReportedWithoutChangingGeometry()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        var path=Report().PathUnknown!; capture.RecordPath(path);
        capture.RecordPathDiscovery(new(.3f,.2f),LayerQueryResult.Unknown);
        capture.RecordPathDiscovery(new(.8f,.2f),LayerQueryResult.Success);
        capture.RecordPathAttemptResult(LayerQueryResult.Pending);
        Assert.True(capture.TryFinish(129,10,out var report));
        Assert.Same(path.Layer,report!.PathUnknown!.Layer);
        Assert.Equal(new Vector2(.3f,.2f),report.PathUnknown.DiscoveryTarget);
        Assert.Equal(LayerQueryResult.Unknown,report.PathUnknown.DiscoveryResult);
        Assert.Equal(LayerQueryResult.Pending,report.PathUnknown.AttemptResult);
    }
    [Fact] public void CellUnknownWithSuccessfulDiscoveryAndPendingRetryIsCapturedExactly()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        var candidate=Cell(Plane()) with {Result=LayerQueryResult.Unknown,Reason="Uncovered footprint",
            DiscoveryTarget=new(.3f,.2f),DiscoveryResult=LayerQueryResult.Success,AttemptResult=LayerQueryResult.Pending};
        capture.RecordCell(candidate);
        Assert.True(capture.TryFinish(129,10,out var report)); Assert.Same(candidate,report!.CellPending);
        Assert.Equal(LayerQueryResult.Unknown,report.CellPending!.Result);
        Assert.Equal(LayerQueryResult.Success,report.CellPending.DiscoveryResult);
    }
    [Fact] public void TransientCellUnknownThatUltimatelySucceedsIsNotThePendingRecord()
    {
        var capture=new ClothCollisionCapture(); capture.Begin(129,0);
        capture.RecordCell(Cell(Plane()) with {Result=LayerQueryResult.Unknown,AttemptResult=LayerQueryResult.Success});
        Assert.True(capture.WantsCell);
    }
    [Fact] public void WriterCreatesUniqueBoundedReportsWithoutChangingExistingFile()
    {
        var dir=Directory.CreateTempSubdirectory("rug-replay-test-").FullName;
        try
        {
            var first=ClothCollisionReportFile.WriteNew(dir,Report(),default);
            var bytes=File.ReadAllBytes(System.IO.Path.Combine(dir,first));
            var second=ClothCollisionReportFile.WriteNew(dir,Report(),default);
            Assert.NotEqual(first,second); Assert.Equal(bytes,File.ReadAllBytes(System.IO.Path.Combine(dir,first)));
            Assert.Equal(129u,ClothCollisionReportFile.Read(System.IO.Path.Combine(dir,first)).Zone);
            Assert.True(bytes.Length<=ClothCollisionReportFile.MaximumBytes);
        }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public void CancelledWriterDoesNotCreateAFile()
    {
        var dir=Directory.CreateTempSubdirectory("rug-replay-cancel-").FullName;
        try
        {
            using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>ClothCollisionReportFile.WriteNew(dir,Report(),cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir,true); }
    }
}
