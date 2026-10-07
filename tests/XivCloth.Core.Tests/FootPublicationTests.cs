using System.Numerics;
using XivCloth.Core;

public sealed class FootPublicationTests
{
    private static readonly FootProxyIdentity Owner=new(128,42,7,1);
    private static FootProxyPose Pose(long sequence,double time,float sole=.1f,FootProxyIdentity? identity=null,float x=0)
    {
        FootMarkerEnvelope[] markers=[new(new(x-.1f,0),sole,.1f),new(new(x+.1f,0),sole,.1f),
            new(new(-.1f,1.7f),sole,.1f),new(new(.1f,1.7f),sole,.1f)];
        Assert.True(FootProxyPose.TryCreate(identity??Owner,sequence,time,markers,out var result));return result!;
    }
    private static FootProxyMotion Motion(FootProxyPose a,FootProxyPose b)
    {Assert.True(FootProxyMotion.TryCreate(a,b,b.SampledAt,out var result));return result!;}
    private static XpbdCloth Solver()=>new(new([new(-1,.04f,-1),new(0,.04f,1),new(1,.04f,-1)],new Vector2[3],[0,1,2],
        [1,1,1],[new(0,1,0),new(1,2,0),new(2,0,0)]),new(){Gravity=new(0,-1,0)});
    private static (XpbdCloth Cloth,FootProxyPose Accepted) Admitted()
    {
        var solver=Solver();var a=Pose(1,10);var b=Pose(2,10+1d/80);var motion=Motion(a,b);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt).Status);
        return (solver,b);
    }

    [Fact]
    public void SuccessfulOwnedPublicationKeepsExactCurrentXyzAndCaptureIdentities()
    {
        var (solver,accepted)=Admitted();var late=Pose(3,accepted.SampledAt+1d/240);var before=solver.Capture();
        var result=solver.CaptureForFeet(accepted,late,late.SampledAt);
        Assert.Equal(XpbdStatus.Ready,result.Status);var frame=Assert.IsType<XpbdFrame>(result.Frame);
        Assert.Equal(before.Positions.ToArray(),frame.Positions.ToArray());Assert.Equal(before.Indices.ToArray(),frame.Indices.ToArray());
        Assert.Equal(before.UV.ToArray(),frame.UV.ToArray());Assert.Equal(before.SceneGeneration,frame.SceneGeneration);
        var binding=Assert.IsType<XpbdFootPublicationBinding>(frame.FootBinding);
        Assert.Same(accepted,binding.AcceptedFeet);Assert.Same(late,binding.InspectedFeet);Assert.Equal(late.SampledAt,binding.InspectedAt);
        Assert.Equal(.002f,binding.Clearance);Assert.False(frame.EndpointInterpolationAllowed);Assert.Null(before.FootBinding);
        Assert.True(binding.CanPresent(accepted,late,frame.SceneGeneration,late.SampledAt));
        var saved=frame.Positions.ToArray();var motion=Motion(accepted,late);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,late.SampledAt).Status);
        Assert.Equal(saved,frame.Positions.ToArray());Assert.NotEqual(saved,solver.Capture().Positions.ToArray());
        Assert.False(binding.CanPresent(late,late,frame.SceneGeneration,late.SampledAt));
    }

    [Fact]
    public void LateSoleCollidingWithFaceInteriorRefusesEvenThoughAllCornersMiss()
    {
        var (solver,accepted)=Admitted();var late=Pose(3,accepted.SampledAt+1d/120,.03f);var before=solver.Capture().Positions.ToArray();
        Assert.All(before,p=>Assert.True(Vector3.Distance(p,late.Capsules[0].A)>.5f));
        var result=solver.CaptureForFeet(accepted,late,late.SampledAt);
        Assert.Equal(XpbdStatus.CollisionUnproven,result.Status);Assert.Null(result.Frame);Assert.True(result.SweepNodes>0);
        Assert.Equal(before,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void SameAcceptedObjectWorksButAnEqualSequenceCloneCannotRelabelIt()
    {
        var (solver,accepted)=Admitted();var clone=Pose(accepted.Sequence,accepted.SampledAt);
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForFeet(accepted,accepted,accepted.SampledAt).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(clone,accepted,accepted.SampledAt).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(accepted,clone,accepted.SampledAt).Status);
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void AChangedActorZoneInstanceOrModelCannotAdmitOldMaterial(int field)
    {
        var (solver,accepted)=Admitted();var changed=field switch
        {0=>Owner with{Actor=43},1=>Owner with{Zone=129},2=>Owner with{Instance=8},_=>Owner with{ModelGeneration=2}};
        var late=Pose(3,accepted.SampledAt+1d/120,identity:changed);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(accepted,late,late.SampledAt).Status);
    }

    [Fact]
    public void SequenceTimeFreshnessAndMotionBoundsDoNotAcceptArbitraryLaterFrames()
    {
        var (solver,accepted)=Admitted();var time=accepted.SampledAt+1d/120;
        foreach(var late in new[]{Pose(4,time),Pose(3,accepted.SampledAt),Pose(3,accepted.SampledAt-.001),Pose(3,time,x:2),Pose(3,accepted.SampledAt+.1)})
            Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(accepted,late,Math.Max(time,late.SampledAt)).Status);
        var valid=Pose(3,time);
        foreach(var now in new[]{double.NaN,double.PositiveInfinity,accepted.SampledAt-.001,time-.001,accepted.SampledAt+.04})
            Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(accepted,valid,now).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(accepted,null,time).Status);
    }

    [Fact]
    public void AReadThatPredatesTheAcceptedSimulationCheckIsNotALateCapture()
    {
        var solver=Solver();var a=Pose(1,10);var b=Pose(2,10+1d/120);var motion=Motion(a,b);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,10.02).Status);
        var early=Pose(3,10.016);Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(b,early,10.021).Status);
        var late=Pose(3,10.021);Assert.Equal(XpbdStatus.Ready,solver.CaptureForFeet(b,late,10.022).Status);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void RefusedAndSuccessfulReadOnlyAdmissionNeverConsumeLateSequenceOrClock(bool budgetFailure)
    {
        var (tested,accepted)=Admitted();var control=Solver();var initial=Pose(1,10);var admitted=Motion(initial,accepted);
        Assert.Equal(XpbdStatus.Ready,control.AdvanceWithFeet(admitted.Duration,MeasuredTriangleScene.Empty,admitted,accepted.SampledAt).Status);
        var late=Pose(3,accepted.SampledAt+1d/240);var old=tested.Capture().Positions.ToArray();
        var result=tested.CaptureForFeet(accepted,late,late.SampledAt,maximumWork:budgetFailure?0:null);
        Assert.Equal(budgetFailure?XpbdStatus.WorkBudgetExceeded:XpbdStatus.Ready,result.Status);
        Assert.Equal(old,tested.Capture().Positions.ToArray());Assert.Equal(control.SceneGeneration,tested.SceneGeneration);
        var next=Motion(accepted,late);
        var actual=tested.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,late.SampledAt);
        var expected=control.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,late.SampledAt);
        Assert.Equal(expected,actual);Assert.Equal(1,actual.Substeps);
        Assert.Equal(control.Capture().Positions.ToArray(),tested.Capture().Positions.ToArray());
    }

    [Fact]
    public void FailedGeometryAdmissionDoesNotTouchMomentumOrAnyAcceptedHistory()
    {
        var (tested,accepted)=Admitted();var control=Solver();var initial=Pose(1,10);var admitted=Motion(initial,accepted);
        Assert.Equal(XpbdStatus.Ready,control.AdvanceWithFeet(admitted.Duration,MeasuredTriangleScene.Empty,admitted,accepted.SampledAt).Status);
        var blocked=Pose(3,accepted.SampledAt+1d/120,.03f);
        Assert.Equal(XpbdStatus.CollisionUnproven,tested.CaptureForFeet(accepted,blocked,blocked.SampledAt).Status);
        var safe=Pose(3,accepted.SampledAt+1d/240);var next=Motion(accepted,safe);
        Assert.Equal(control.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,safe.SampledAt),
            tested.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,safe.SampledAt));
        Assert.Equal(control.Capture().Positions.ToArray(),tested.Capture().Positions.ToArray());
    }

    [Fact]
    public void BindingExpiresAndRejectsChangedReferencesOrSceneWithoutRetimestamping()
    {
        var (solver,accepted)=Admitted();var late=Pose(3,accepted.SampledAt+1d/120);
        var frame=Assert.IsType<XpbdFrame>(solver.CaptureForFeet(accepted,late,late.SampledAt).Frame);var binding=frame.FootBinding!;
        Assert.Equal(accepted.SampledAt+FootProxyPose.MaximumAgeSeconds,binding.ExpiresAt);
        Assert.True(binding.CanPresent(accepted,late,frame.SceneGeneration,binding.ExpiresAt));
        foreach(var now in new[]{late.SampledAt-.001,double.NaN,binding.ExpiresAt+.001})
            Assert.False(binding.CanPresent(accepted,late,frame.SceneGeneration,now));
        Assert.False(binding.CanPresent(accepted,late,frame.SceneGeneration+1,late.SampledAt));
        Assert.False(binding.CanPresent(accepted,Pose(3,late.SampledAt),frame.SceneGeneration,late.SampledAt));
        Assert.False(binding.CanPresent(Pose(2,accepted.SampledAt),late,frame.SceneGeneration,late.SampledAt));
    }

    [Fact]
    public void NoUnadmittedResetOrSupersededSimulationStateCanPublish()
    {
        var solver=Solver();var a=Pose(1,10);var b=Pose(2,10+1d/120);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(a,b,b.SampledAt).Status);
        var motion=Motion(a,b);Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(a,b,b.SampledAt).Status);
        solver.Reset();Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(b,b,b.SampledAt).Status);
    }

    [Fact]
    public void CallerMayOnlyLowerTheExistingBudget()
    {
        var (solver,accepted)=Admitted();
        foreach(var budget in new[]{-1,100_001,int.MaxValue})
            Assert.Equal(XpbdStatus.RejectedInput,solver.CaptureForFeet(accepted,accepted,accepted.SampledAt,budget).Status);
        var refused=solver.CaptureForFeet(accepted,accepted,accepted.SampledAt,0);
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,refused.Status);Assert.Null(refused.Frame);Assert.Equal(0,refused.WorkUsed);
        var okay=solver.CaptureForFeet(accepted,accepted,accepted.SampledAt,100);
        Assert.Equal(XpbdStatus.Ready,okay.Status);Assert.InRange(okay.WorkUsed,1,100);
    }
}
