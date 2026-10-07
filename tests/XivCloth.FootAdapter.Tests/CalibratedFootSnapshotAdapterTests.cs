using System.Numerics;
using XivCloth.Core;
using XivRug.Prototype;
using XivSurface.Core;

namespace XivCloth.FootAdapter.Tests;

public sealed class CalibratedFootSnapshotAdapterTests
{
    private static readonly RawFootIdentity Identity=new(new(3,12,34,2),56,78,Vector3.One,1);
    private static RawFootFrame Raw(long sequence,double time,float lift=0,Quaternion? rotation=null,RawFootIdentity? identity=null)
    {
        var q=rotation??Quaternion.Identity;var id=identity??Identity;
        RawFootMarker[] markers=[new(new(-.15f,.04f+lift,-.1f),q),new(new(-.15f,.06f+lift,.1f),q),
            new(new(.15f,.04f+lift,-.1f),q),new(new(.15f,.06f+lift,.1f),q)];
        Assert.True(RawFootFrame.TryCapture(id,id,sequence,time,time+.0001,Vector3.Zero,markers,out var frame));return frame!;
    }
    private static FootSupportObservation Support(RawFootFrame frame,float height=0)
    {
        FloorTriangle floor=new(new(-10,height,-10),new(-10,height,20),new(20,height,-10));
        Assert.True(FootSupportObservation.TryCreate(frame,1,[floor,floor,floor,floor],out var support));return support!;
    }
    private static FootPlantCalibration Calibration()
    {
        Assert.True(FootPlantCalibration.TryCreate([Support(Raw(1,1)),Support(Raw(2,1.02)),Support(Raw(3,1.04))],1.041,out var c));return c!;
    }
    private static CalibratedFootPose Evaluate(FootPlantCalibration c,RawFootFrame raw,float floor=0)
    {Assert.True(c.TryEvaluate(raw,Support(raw,floor),raw.ReadCompletedAt,out var p));return p!;}
    private static FootProxyPose Capture(CalibratedFootSnapshotAdapter adapter,CalibratedFootPose p)
    {Assert.True(adapter.TryCapture(p,p.Source,p.Source.ReadCompletedAt,out var body));return body!;}

    [Fact] public void ActualRawThroughCalibrationPreservesLiftRotationAndExactReferences()
    {
        var c=Calibration();var raw=Raw(4,1.06,.3f,Quaternion.CreateFromAxisAngle(Vector3.UnitX,.5f));var p=Evaluate(c,raw);
        var body=Capture(new(),p);
        for(var i=0;i<2;i++)Assert.Equal(new FootCapsule(p.Capsules[i].A,p.Capsules[i].B,p.Capsules[i].Radius),body.Capsules[i]);
        Assert.NotEqual(body.Capsules[0].A.Y,body.Capsules[0].B.Y);
        Assert.Same(raw,body.CaptureBinding);Assert.NotNull(body.Identity.ModelBinding);
        Assert.Equal(raw.Sequence,body.Sequence);Assert.Equal(raw.SampledAt,body.SampledAt);
        Assert.Equal((uint)3,body.Identity.Zone);Assert.Equal((ulong)12,body.Identity.Actor);
        Assert.Equal((ulong)34,body.Identity.Instance);Assert.Equal(2,body.Identity.ModelGeneration);
    }

    [Fact] public void SameExactCaptureReusesBodyButConsecutiveFramesRemainOneModel()
    {
        var a=new CalibratedFootSnapshotAdapter();var c=Calibration();var raw=Raw(4,1.06);var p=Evaluate(c,raw);
        var before=Capture(a,p);Assert.Same(before,Capture(a,Evaluate(c,raw)));
        var after=Capture(a,Evaluate(c,Raw(5,1.08,.01f)));
        Assert.Equal(before.Identity,after.Identity);Assert.NotSame(before.CaptureBinding,after.CaptureBinding);
        Assert.True(FootProxyMotion.TryCreate(before,after,1.081,out _));
    }

    [Fact] public void IdenticalNewCalibrationBreaksAcceptedMotion()
    {
        var a=new CalibratedFootSnapshotAdapter();var before=Capture(a,Evaluate(Calibration(),Raw(4,1.06)));
        var after=Capture(a,Evaluate(Calibration(),Raw(5,1.08)));
        Assert.NotEqual(before.Identity,after.Identity);
        Assert.False(FootProxyMotion.TryCreate(before,after,1.081,out _));
    }

    [Fact] public void AdapterResetOrReplacementCannotReuseOldModelToken()
    {
        var a=new CalibratedFootSnapshotAdapter();var c=Calibration();var before=Capture(a,Evaluate(c,Raw(4,1.06)));
        a.Invalidate();var after=Capture(a,Evaluate(c,Raw(5,1.08)));
        Assert.NotEqual(before.Identity,after.Identity);
        var separate=Capture(new(),Evaluate(c,Raw(5,1.08)));
        Assert.NotEqual(after.Identity,separate.Identity);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MissingStaleFutureOrRelabeledCurrentRefusesAndBreaksContinuity(int mode)
    {
        var a=new CalibratedFootSnapshotAdapter();var c=Calibration();var raw=Raw(4,1.06);var p=Evaluate(c,raw);
        var before=Capture(a,p);
        var current=mode==0?null:mode==3?Raw(4,1.06):raw;
        var now=mode==1?1.10:mode==2?1.059:1.061;
        Assert.False(a.TryCapture(p,current,now,out var failed));Assert.Null(failed);
        var after=Capture(a,Evaluate(c,Raw(5,1.08)));
        Assert.NotEqual(before.Identity,after.Identity);
    }

    [Fact] public void SameSequenceCloneAndSequenceGapCannotReconnect()
    {
        var a=new CalibratedFootSnapshotAdapter();var c=Calibration();var before=Capture(a,Evaluate(c,Raw(4,1.06)));
        Assert.False(a.TryCapture(Evaluate(c,Raw(4,1.06)),Raw(4,1.06),1.061,out _));
        before=Capture(a,Evaluate(c,Raw(4,1.06)));
        var clone=Raw(4,1.06);
        Assert.False(a.TryCapture(Evaluate(c,clone),clone,1.061,out _));
        before=Capture(a,Evaluate(c,Raw(4,1.06)));var gap=Raw(6,1.10);
        Assert.False(a.TryCapture(Evaluate(c,gap),gap,1.101,out _));
        var after=Capture(a,Evaluate(c,Raw(5,1.08)));Assert.NotEqual(before.Identity,after.Identity);
    }

    [Fact] public void WrongOwnerCurrentCannotRelabelEvaluatedCapture()
    {
        var c=Calibration();var p=Evaluate(c,Raw(4,1.06));
        var wrong=Raw(4,1.06,identity:Identity with{Actor=Identity.Actor with{PlayerId=99}});
        Assert.False(new CalibratedFootSnapshotAdapter().TryCapture(p,wrong,1.061,out _));
    }

    [Fact] public void CompressedAndConflictingSupportDoNotMoveProxyOrAuthorizeSolver()
    {
        var c=Calibration();var raw=Raw(4,1.06);var compressed=Evaluate(c,raw);var conflict=Evaluate(c,raw,.02f);
        Assert.Equal(FootPlantState.CompressedPlant,compressed.State);Assert.Equal(FootPlantState.SupportConflict,conflict.State);
        var a=Capture(new(),compressed);var b=Capture(new(),conflict);
        Assert.Equal(a.Capsules.ToArray(),b.Capsules.ToArray());
        // This adapter intentionally returns geometry, not an admission verdict.
        Assert.InRange(a.Capsules[0].A.Y-a.Capsules[0].Radius,-.000001f,.000001f);
    }

    [Fact] public void MissingEvaluationRefusesWithoutInventingAFallback()
    {Assert.False(new CalibratedFootSnapshotAdapter().TryCapture(null,Raw(4,1.06),1.061,out var p));Assert.Null(p);}

    [Fact] public void BackwardCheckOnSameExactCaptureRefuses()
    {
        var a=new CalibratedFootSnapshotAdapter();var c=Calibration();var raw=Raw(4,1.06);var p=Evaluate(c,raw);
        Assert.True(a.TryCapture(p,raw,1.065,out var before));
        Assert.False(a.TryCapture(p,raw,1.064,out var failed));Assert.Null(failed);
        var after=Capture(a,Evaluate(c,Raw(5,1.08)));Assert.NotEqual(before!.Identity,after.Identity);
    }
}
