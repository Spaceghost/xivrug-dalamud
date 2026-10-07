using System.Numerics;
using XivCloth.Core;

public sealed class MeasuredFootIntervalTests
{
    private static readonly FootProxyIdentity Identity=new(3,12,34,1);
    private static readonly Vector3[] Rest=[new(-.2f,.5f,-.2f),new(0,.5f,.2f),new(.2f,.5f,-.2f)];
    private static FootProxyPose Pose(long sequence,double time,float x=4,float sole=.5f,FootProxyIdentity? identity=null)
    {
        FootMarkerEnvelope[] m=[new(new(x,-.05f),sole,.1f),new(new(x,.05f),sole,.1f),
            new(new(6,-.05f),sole,.1f),new(new(6,.05f),sole,.1f)];
        Assert.True(FootProxyPose.TryCreate(identity??Identity,sequence,time,m,out var result));return result!;
    }
    private static MeasuredFootInterval Interval(FootProxyPose before,FootProxyPose after,double? now=null)
    {Assert.True(MeasuredFootInterval.TryCreate(before,after,now??after.SampledAt,out var result));return result!;}
    private static XpbdDefinition Definition(bool pinned=false)=>new(Rest,new Vector2[3],[0,1,2],
        pinned?[0,0,0]:[1,1,1],[new(0,1,0),new(1,2,0),new(2,0,0)]);
    private static MeasuredTriangleScene Empty()=>new(1,[]);
    private static XpbdCloth Solver(XpbdDefinition? d=null,XpbdSettings? settings=null)=>new(d??Definition(),settings??new(){Gravity=default,Drag=0});

    [Fact] public void Real25FpsUsesHistoricalBeforeAndFreshAfterWithoutChangingStrictFactory()
    {
        var a=Pose(1,10);var b=Pose(2,10.04);
        Assert.False(a.FreshAt(b.SampledAt));
        Assert.False(FootProxyMotion.TryCreate(a,b,b.SampledAt,out _));
        var measured=Interval(a,b);Assert.Equal(5,measured.Substeps);
        Assert.InRange(measured.StepSeconds,.007999999,.008000001);
        Assert.Same(a,measured.Before);Assert.Same(b,measured.After);
        var solver=Solver();var result=solver.AdvanceMeasuredFeet(Empty(),measured,b.SampledAt);
        Assert.Equal(XpbdStatus.Ready,result.Status);Assert.Equal(5,result.Substeps);Assert.Equal(0,result.DroppedSeconds);
        Assert.Equal(Rest,solver.Capture().Positions.ToArray());
        var publication=solver.CaptureForFeet(b,b,b.SampledAt);
        Assert.Equal(XpbdStatus.Ready,publication.Status);Assert.Same(b,publication.Frame!.FootBinding!.AcceptedFeet);
    }

    [Theory]
    [InlineData(.001,1)] [InlineData(.00625,1)] [InlineData(.0125,2)]
    [InlineData(.025,3)] [InlineData(.04,5)] [InlineData(.05,6)]
    public void ExactObservedDurationIsCoveredByBoundedEqualSteps(double duration,int steps)
    {
        var interval=Interval(Pose(1,1),Pose(2,1+duration));
        Assert.Equal(steps,interval.Substeps);
        Assert.Equal(interval.Duration,interval.StepSeconds*interval.Substeps,12);
        var r=Solver().AdvanceMeasuredFeet(Empty(),interval,interval.After.SampledAt);
        Assert.Equal(XpbdStatus.Ready,r.Status);Assert.Equal(steps,r.Substeps);Assert.Equal(0,r.DroppedSeconds);
    }

    [Fact] public void LongRunningClockBoundaryDoesNotInventASeventhStep()
    {
        const double start=194*24*60*60;
        var interval=Interval(Pose(1,start),Pose(2,start+.05));
        Assert.Equal(6,interval.Substeps);
        Assert.Equal(XpbdStatus.Ready,Solver().AdvanceMeasuredFeet(Empty(),interval,interval.After.SampledAt).Status);
    }

    [Fact] public void CoarseClockCannotRoundFiftyMillisecondsIntoALongerInterval()
    {
        const double coarse=140737488355328; // 2^47: clock quantum31.25ms
        var a=Pose(1,coarse);var b=Pose(2,coarse+.05);
        Assert.True(b.SampledAt-a.SampledAt>.05);
        Assert.False(MeasuredFootInterval.TryCreate(a,b,b.SampledAt,out _));
        // The before endpoint can still have finer resolution immediately
        // below a power-of-two boundary while AFTER crosses the admission cap.
        const double boundary=8589934592; // 2^33: ULP changes from.953us to1.907us
        a=Pose(1,boundary-.02);b=Pose(2,boundary+.02);
        Assert.True(Math.BitIncrement(a.SampledAt)-a.SampledAt<=MeasuredFootInterval.MaximumClockQuantum);
        Assert.True(Math.BitIncrement(b.SampledAt)-b.SampledAt>MeasuredFootInterval.MaximumClockQuantum);
        Assert.False(MeasuredFootInterval.TryCreate(a,b,b.SampledAt,out _));
        a=Pose(1,10);b=Pose(2,10+.050001);
        Assert.False(MeasuredFootInterval.TryCreate(a,b,b.SampledAt,out _));
    }

    [Fact] public void InvalidDurationFreshnessOwnerSequenceAndSpeedFailClosed()
    {
        var a=Pose(1,1);var b=Pose(2,1.04);
        Assert.False(MeasuredFootInterval.TryCreate(a,Pose(2,1.051),1.051,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,Pose(2,1),1,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,Pose(2,1.00000001),1.00000001,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,b,1.039,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,b,1.08,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,b,double.NaN,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,Pose(3,1.04),1.04,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,Pose(2,1.04,identity:Identity with{ModelGeneration=2}),1.04,out _));
        Assert.False(MeasuredFootInterval.TryCreate(a,Pose(2,1.04,x:5),1.04,out _));
        Assert.False(MeasuredFootInterval.TryCreate(null,b,1.04,out _));
    }

    [Fact] public void ForceGravityDampingAndVelocityUseActualStepDuration()
    {
        var d=Definition();var settings=new XpbdSettings{Gravity=new(0,-3,0),Drag=2,Iterations=2};
        var input=new XpbdStepInputs(d,[new(1,0,0),new(1,0,0),new(1,0,0)]);
        var interval=Interval(Pose(1,1),Pose(2,1.04));var s=Solver(d,settings);
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),interval,1.04,input).Status);
        var dt=(float)interval.StepSeconds;var p=Rest[0];var v=Vector3.Zero;
        for(var n=0;n<5;n++){v=(v+new Vector3(1,-3,0)*dt)*MathF.Exp(-2*dt);p+=v*dt;}
        Assert.InRange(Vector3.Distance(p,s.Capture().Positions[0]),0,.00001f);
        // A subsequent force-free interval checks that accepted velocity was
        // reconstructed with the same actual dt, not the old1/120 divisor.
        var next=Interval(interval.After,Pose(3,1.08));
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),next,1.08).Status);
        for(var n=0;n<5;n++){v=(v+new Vector3(0,-3,0)*dt)*MathF.Exp(-2*dt);p+=v*dt;}
        Assert.InRange(Vector3.Distance(p,s.Capture().Positions[0]),0,.00003f);
    }

    [Fact] public void ConstantInputsMatchFiveGenuineEightMillisecondIntervals()
    {
        var d=Definition();var input=new XpbdStepInputs(d,[new(1,0,0),new(1,0,0),new(1,0,0)]);
        var whole=Solver(d);var split=Solver(d);var a=Pose(1,1);
        Assert.Equal(XpbdStatus.Ready,whole.AdvanceMeasuredFeet(Empty(),Interval(a,Pose(2,1.04)),1.04,input).Status);
        for(var i=1;i<=5;i++)
        {
            var b=Pose(i+1,1+i*.008);var motion=Interval(a,b);
            Assert.Equal(1,motion.Substeps);
            Assert.Equal(XpbdStatus.Ready,split.AdvanceMeasuredFeet(Empty(),motion,b.SampledAt,input).Status);a=b;
        }
        Assert.Equal(whole.Capture().Positions.ToArray(),split.Capture().Positions.ToArray());
    }

    private static XpbdDefinition FreeTargetDefinition()=>new(Rest,new Vector2[3],[0,1,2],[1,0,0],[new(1,2,0)]);
    [Fact] public void TargetComplianceUsesActualDtSquaredAndPinsRemainExact()
    {
        var d=FreeTargetDefinition();var target=Rest[0]+new Vector3(.02f,0,0);const float compliance=.001f;
        var input=new XpbdStepInputs(d,targets:[new(0,target,compliance),new(1,Rest[1]+Vector3.UnitY,0)]);
        var interval=Interval(Pose(1,1),Pose(2,1.00625));var s=Solver(d,new(){Gravity=default,Drag=0,Iterations=1});
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),interval,interval.After.SampledAt,input).Status);
        var dt=(float)interval.StepSeconds;var expected=Rest[0]+new Vector3(.02f/(1+compliance/(dt*dt)),0,0);
        Assert.InRange(Vector3.Distance(expected,s.Capture().Positions[0]),0,.0000001f);
        Assert.Equal(Rest[1],s.Capture().Positions[1]);Assert.Equal(Rest[2],s.Capture().Positions[2]);
    }

    [Fact] public void ShortStepDoesNotMultiplyHardTargetPullAllowance()
    {
        var d=FreeTargetDefinition();var input=new XpbdStepInputs(d,targets:[new(0,Rest[0]+new Vector3(.5f,0,0),0)]);
        var interval=Interval(Pose(1,1),Pose(2,1.001));var s=Solver(d,new(){Gravity=default,Drag=0,Iterations=16});
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),interval,interval.After.SampledAt,input).Status);
        var displacement=Vector3.Distance(Rest[0],s.Capture().Positions[0]);
        var limit=XpbdStepInputs.MaximumTargetCorrectionPerSubstep*(float)(interval.StepSeconds/XpbdCloth.FixedStep);
        Assert.InRange(displacement,limit-.000001f,limit+.000001f);
    }

    [Fact] public void SweptInteriorCollisionRollsBackAndDoesNotConsumeMeasuredSequenceOrClock()
    {
        var d=Definition(true);var s=Solver(d);var a=Pose(1,1,x:.4f);var collision=Pose(2,1.04,x:-.39f);
        var rejected=s.AdvanceMeasuredFeet(Empty(),Interval(a,collision),collision.SampledAt);
        Assert.NotEqual(XpbdStatus.Ready,rejected.Status);Assert.Equal(Rest,s.Capture().Positions.ToArray());Assert.Equal(0,s.SceneGeneration);
        var safe=Pose(2,1.04,x:.4f);
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),Interval(a,safe),safe.SampledAt).Status);
        Assert.Equal(Rest,s.Capture().Positions.ToArray());
    }

    [Fact] public void WrongReferenceAndBackwardCheckTimeRefuseAfterAcceptance()
    {
        var s=Solver();var a=Pose(1,1);var b=Pose(2,1.04);
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),Interval(a,b),1.05).Status);
        var c=Pose(3,1.045);var next=Interval(b,c,1.05);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,s.AdvanceMeasuredFeet(Empty(),next,1.049).Status);
        var clone=Pose(2,1.04);var later=Pose(3,1.08);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,s.AdvanceMeasuredFeet(Empty(),Interval(clone,later),1.08).Status);
        Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),Interval(b,later),1.08).Status);
    }

    [Fact] public void ClockModeCannotDiscardFixedRemainderAndResetExplicitlySwitches()
    {
        var s=Solver();Assert.Equal(XpbdStatus.Ready,s.Advance(.001,Empty()).Status);
        var interval=Interval(Pose(1,1),Pose(2,1.04));
        Assert.Equal(XpbdStatus.RejectedInput,s.AdvanceMeasuredFeet(Empty(),interval,1.04).Status);
        Assert.Equal(1,s.Advance(XpbdCloth.FixedStep-.001,Empty()).Substeps);
        s.Reset();Assert.Equal(XpbdStatus.Ready,s.AdvanceMeasuredFeet(Empty(),interval,1.04).Status);
        var next=Pose(3,1.05);Assert.True(FootProxyMotion.TryCreate(interval.After,next,1.05,out var strict));
        Assert.Equal(XpbdStatus.RejectedInput,s.AdvanceWithFeet(strict!.Duration,Empty(),strict,1.05).Status);
        s.Reset();Assert.Equal(XpbdStatus.Ready,s.Advance(XpbdCloth.FixedStep,Empty()).Status);
    }

    [Fact] public void ExhaustedBudgetHasNoStateOrTimingModeSideEffect()
    {
        var s=Solver(settings:new(){Gravity=default,CollisionWorkLimit=0});var interval=Interval(Pose(1,1),Pose(2,1.04));
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,s.AdvanceMeasuredFeet(Empty(),interval,1.04).Status);
        Assert.Equal(Rest,s.Capture().Positions.ToArray());Assert.Equal(0,s.SceneGeneration);
        // Strict no-feet empty-scene admission needs no capsule work. Success
        // proves the failed measured call did not secretly claim the clock.
        Assert.Equal(XpbdStatus.Ready,s.Advance(0,Empty()).Status);
    }

    [Fact] public void LaterSubstepFailureRestoresVelocitySequenceAndCheckClockExactly()
    {
        var floor=new MeasuredTriangleScene(1,[new(new(-2,.49f,-2),new(-2,.49f,2),new(2,.49f,-2)),
            new(new(2,.49f,-2),new(-2,.49f,2),new(2,.49f,2))]);
        var d=Definition();var settings=new XpbdSettings{Gravity=new(0,-.5f,0),Drag=0,Iterations=2};
        var tested=Solver(d,settings);var control=Solver(d,settings);
        var a=Pose(1,1,x:.4f);var b=Pose(2,1.04,x:.4f);var warm=Interval(a,b);
        Assert.Equal(XpbdStatus.Ready,tested.AdvanceMeasuredFeet(floor,warm,1.04).Status);
        Assert.Equal(XpbdStatus.Ready,control.AdvanceMeasuredFeet(floor,warm,1.04).Status);
        var accepted=tested.Capture().Positions.ToArray();
        var collision=Pose(3,1.08,x:-.39f,sole:.48f);
        var failure=tested.AdvanceMeasuredFeet(floor,Interval(b,collision),1.09);
        Assert.NotEqual(XpbdStatus.Ready,failure.Status);
        Assert.True(failure.ConstraintSolves>=12); // at least two iterations across two substeps
        Assert.Equal(accepted,tested.Capture().Positions.ToArray());Assert.Equal(1,tested.SceneGeneration);
        var safe=Interval(b,Pose(3,1.08,x:.4f));
        // Earlier than the FAILED check time, but later than the last accepted
        // check. Reusing sequence3 and the exact b must still work.
        Assert.Equal(XpbdStatus.Ready,tested.AdvanceMeasuredFeet(floor,safe,1.08).Status);
        Assert.Equal(XpbdStatus.Ready,control.AdvanceMeasuredFeet(floor,safe,1.08).Status);
        Assert.Equal(control.Capture().Positions.ToArray(),tested.Capture().Positions.ToArray());
    }

    [Fact] public void DeformingStretchShearBendAndTargetsMatchActualSubstepGrouping()
    {
        Vector3[] rest=[new(-.2f,1,-.2f),new(.2f,1,-.2f),new(-.2f,1,.2f),new(.2f,1.1f,.2f)];
        var d=new XpbdDefinition(rest,new Vector2[4],[0,2,1,1,2,3],[0,1,1,1],
            [new(0,1,MaterialEdge.Stretch),new(0,2,MaterialEdge.Stretch),new(1,3,MaterialEdge.Stretch),
             new(2,3,MaterialEdge.Stretch),new(1,2,MaterialEdge.Shear)]);
        var input=new XpbdStepInputs(d,[Vector3.Zero,new(0,-2,0),new(1,0,0),new(0,-1,1)],
            [new(3,rest[3]+new Vector3(.03f,.05f,.01f),.001f)]);
        var whole=Solver(d);var split=Solver(d);var a=Pose(1,1,sole:2);
        Assert.Equal(XpbdStatus.Ready,whole.AdvanceMeasuredFeet(Empty(),Interval(a,Pose(2,1.04,sole:2)),1.04,input).Status);
        for(var i=1;i<=5;i++)
        {
            var b=Pose(i+1,1+i*.008,sole:2);
            Assert.Equal(XpbdStatus.Ready,split.AdvanceMeasuredFeet(Empty(),Interval(a,b),b.SampledAt,input).Status);a=b;
        }
        Assert.Equal(whole.Capture().Positions.ToArray(),split.Capture().Positions.ToArray());
        Assert.Equal(rest[0],whole.Capture().Positions[0]);
        Assert.NotEqual(rest[3],whole.Capture().Positions[3]);
    }
}
