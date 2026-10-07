using System.Numerics;
using XivCloth.Core;

public sealed class FootProxyTests(ITestOutputHelper output)
{
    private static readonly FootProxyIdentity Identity=new(128,42,7,1);
    private static FootProxyPose Pose(long sequence,double time,float sole,float x=0,float radius=.1f)
    {
        FootMarkerEnvelope[] markers=[new(new(x-.1f,0),sole,radius),new(new(x+.1f,0),sole,radius),
            new(new(-.1f,1.7f),sole,radius),new(new(.1f,1.7f),sole,radius)];
        Assert.True(FootProxyPose.TryCreate(Identity,sequence,time,markers,out var pose));return Assert.IsType<FootProxyPose>(pose);
    }
    private static FootProxyMotion Motion(FootProxyPose a,FootProxyPose b,double? now=null)
    {Assert.True(FootProxyMotion.TryCreate(a,b,now??b.SampledAt,out var motion));return Assert.IsType<FootProxyMotion>(motion);}
    private static readonly Vector3[] Rest=[new(-1,.04f,-1),new(0,.04f,1),new(1,.04f,-1)];
    private static XpbdDefinition Definition(Vector3[]? points=null,bool pinned=false)=>new(points??Rest,new Vector2[3],[0,1,2],
        pinned?[0,0,0]:[1,1,1],[new(0,1,0),new(1,2,0),new(2,0,0)]);
    private static MeasuredTriangleScene Floor()=>new(1,[new(new(-2,0,-2),new(-2,0,2),new(2,0,-2)),new(new(2,0,-2),new(-2,0,2),new(2,0,2))]);
    private static MeasuredTriangle Face(Vector3[] p)=>new(p[0],p[1],p[2]);
    private static SweepVerdict Check(MeasuredTriangle a,MeasuredTriangle b,FootCapsule c,FootCapsule d)
    {var work=new CollisionWork(100_000);return CapsuleSweep.Check(a,b,c,d,.002,ref work);}

    [Fact]
    public void ExistingEnvelopeBuildsTwoOwnedThreeDimensionalCapsuleProxies()
    {
        var pose=Pose(1,10,.03f);var capsule=pose.Capsules[0];
        Assert.Equal(2,pose.Capsules.Length);Assert.Equal(Identity,pose.Identity);
        Assert.Equal(-.1f,capsule.A.X);Assert.Equal(.1f,capsule.B.X);Assert.Equal(.1f,capsule.Radius);
        Assert.True((double)capsule.A.Y-capsule.Radius<=.03f);
        Assert.InRange(.03f-((double)capsule.A.Y-capsule.Radius),0,.000001);
        Assert.True(capsule.A.Y>0); // volume, not a scalar per-cloth-vertex ceiling
    }

    [Fact]
    public void ProxyOwnsCopiesAndRejectsPartialFallbackOrMalformedContacts()
    {
        FootMarkerEnvelope[] markers=[new(new(-.1f,0),.03f,.22f),new(new(.1f,0),.03f,.22f),new(new(-.1f,1),.03f,.22f),new(new(.1f,1),.03f,.22f)];
        Assert.True(FootProxyPose.TryCreate(Identity,1,10,markers,out var pose));var before=Assert.IsType<FootProxyPose>(pose).Capsules[0];
        markers[0]=new(new(7),5,3.35f);Assert.Equal(before,pose.Capsules[0]);
        Assert.False(FootProxyPose.TryCreate(Identity,1,10,markers,out _));
        Assert.False(FootProxyPose.TryCreate(Identity,1,10,markers[..2],out _));
        Assert.False(FootProxyPose.TryCreate(default,1,10,[],out _));
        Assert.False(FootProxyPose.TryCreate(Identity,0,10,[],out _));
        Assert.False(FootProxyPose.TryCreate(Identity,1,double.NaN,[],out _));
    }

    [Fact]
    public void OwnerModelSequenceAgeAndMotionBoundsFailClosed()
    {
        var a=Pose(1,10,.03f);var b=Pose(2,10+1d/60,.04f);
        Assert.True(FootProxyMotion.TryCreate(a,b,b.SampledAt,out _));
        Assert.False(FootProxyMotion.TryCreate(a,null,b.SampledAt,out _));
        Assert.False(FootProxyMotion.TryCreate(a,b,11,out _));
        Assert.False(FootProxyMotion.TryCreate(a,b,9,out _));
        Assert.False(FootProxyMotion.TryCreate(a,Pose(3,b.SampledAt,.04f),b.SampledAt,out _));
        Assert.False(FootProxyMotion.TryCreate(a,Pose(2,10,.04f),10,out _));
        Assert.False(FootProxyMotion.TryCreate(a,Pose(2,b.SampledAt,.04f,1),b.SampledAt,out _));
        FootMarkerEnvelope[] markers=[new(new(-.1f,0),.04f,.1f),new(new(.1f,0),.04f,.1f),new(new(-.1f,1.7f),.04f,.1f),new(new(.1f,1.7f),.04f,.1f)];
        Assert.True(FootProxyPose.TryCreate(Identity with{ModelGeneration=2},2,b.SampledAt,markers,out var other));
        Assert.False(FootProxyMotion.TryCreate(a,other,b.SampledAt,out _));
    }

    [Fact]
    public void WholeFaceInsideSoleFootprintIsDetectedWhenEveryCornerMisses()
    {
        var high=Pose(1,10,.1f);var low=Pose(2,10+1d/60,.03f);var cloth=Face(Rest);
        foreach(var p in Rest)Assert.True(Vector3.Distance(p,low.Capsules[0].A)>.5f);
        Assert.Equal(SweepVerdict.Clear,Check(cloth,cloth,high.Capsules[0],high.Capsules[0]));
        Assert.Equal(SweepVerdict.Unproven,Check(cloth,cloth,high.Capsules[0],low.Capsules[0]));
    }

    [Fact]
    public void DescendingSolePushesWholeFaceWhileAllCornersRemainOutsideFoot()
    {
        var a=Pose(1,10,.1f);var b=Pose(2,10+1d/60,.03f);var motion=Motion(a,b);
        var solver=new XpbdCloth(Definition(),new(){Gravity=default,Iterations=12});
        var result=solver.AdvanceWithFeet(motion.Duration,Floor(),motion,b.SampledAt);
        Assert.Equal(XpbdStatus.Ready,result.Status);var positions=solver.Capture().Positions.ToArray();
        Assert.True(positions.Average(p=>p.Y)<.035f);Assert.All(positions,p=>Assert.True(p.Y>=.00399f));
        Assert.Equal(SweepVerdict.Clear,Check(Face(positions),Face(positions),b.Capsules[0],b.Capsules[0]));
        output.WriteLine($"landing wholeface: {string.Join(';',positions)}, pairs={result.TrianglePairs}, sweeps={result.SweepNodes}");
    }

    [Fact]
    public void SimultaneousFloorAndRaisedPlantThenLiftSlideAndLandStayContinuous()
    {
        var rest=Rest.Select(p=>new Vector3(p.X,.004f,p.Z)).ToArray();var solver=new XpbdCloth(Definition(rest),new(){Gravity=default});
        var pose=Pose(1,10,.014f);var sequence=1L;
        foreach(var state in new[]{(.014f,0f),(.04f,0f),(.04f,.1f),(.014f,.1f)})
        {
            var next=Pose(++sequence,pose.SampledAt+1d/120,state.Item1,state.Item2);var motion=Motion(pose,next);
            Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,Floor(),motion,next.SampledAt).Status);
            Assert.Equal(rest,solver.Capture().Positions.ToArray());pose=next;
        }
        Assert.False(solver.Capture().EndpointInterpolationAllowed);
    }

    [Fact]
    public void SoleEstimateBelowMeasuredFloorRefusesInsteadOfMovingEitherBoundary()
    {
        var rest=Rest.Select(p=>new Vector3(p.X,.004f,p.Z)).ToArray();var solver=new XpbdCloth(Definition(rest),new(){Gravity=default,Iterations=12});
        var a=Pose(1,10,.04f);var b=Pose(2,10+1d/60,-.02f);var motion=Motion(a,b);
        var result=solver.AdvanceWithFeet(motion.Duration,Floor(),motion,b.SampledAt);
        Assert.NotEqual(XpbdStatus.Ready,result.Status);Assert.Equal(rest,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
        Assert.True(b.Capsules[0].A.Y-b.Capsules[0].Radius<0);
        output.WriteLine($"measured floor0 / estimated sole-.02 squeeze: {result.Status}, wholecall rolled back.");
    }

    [Fact]
    public void CapsuleCanPushAFreeVerticalSheetHorizontallyNotOnlyClampY()
    {
        Vector3[] points=[new(0,0,-.3f),new(0,.3f,0),new(0,0,.3f)];
        var solver=new XpbdCloth(Definition(points),new(){Gravity=default,Iterations=12});
        var a=Pose(1,10,0,-.24f);var b=Pose(2,10+1d/60,0,-.16f);var motion=Motion(a,b);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt).Status);
        Assert.Contains(solver.Capture().Positions.ToArray(),p=>p.X>.01f);
    }

    [Fact]
    public void MovingFootCannotTunnelThroughPinnedFaceWithClearEndpointVolumes()
    {
        Vector3[] points=[new(0,-.2f,-.4f),new(0,.4f,0),new(0,-.2f,.4f)];var cloth=Face(points);
        var a=Pose(1,10,0,-.4f);var b=Pose(2,10+1d/30,0,.4f);
        // This oversized speed is deliberately rejected before any solving.
        Assert.False(FootProxyMotion.TryCreate(a,b,b.SampledAt,out _));
        a=Pose(1,10,0,-.3f);b=Pose(2,10+1d/30,0,.3f);var motion=Motion(a,b);
        Assert.Equal(SweepVerdict.Clear,Check(cloth,cloth,a.Capsules[0],a.Capsules[0]));
        Assert.Equal(SweepVerdict.Clear,Check(cloth,cloth,b.Capsules[0],b.Capsules[0]));
        Assert.Equal(SweepVerdict.Unproven,Check(cloth,cloth,a.Capsules[0],b.Capsules[0]));
        var solver=new XpbdCloth(Definition(points,true),new(){Gravity=default});
        var result=solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt);
        Assert.Equal(XpbdStatus.CollisionUnproven,result.Status);Assert.True(result.ConstraintSolves>0);Assert.Equal(0,result.Substeps);
        Assert.Equal(points,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
    }

    [Fact]
    public void RelativeTranslationLeavesSeparationVerdictUnchanged()
    {
        var body=new FootCapsule(new(-.1f,.2f,0),new(.1f,.2f,0),.1f);var cloth=Face(Rest);var translation=new Vector3(.3f,.2f,-.1f);
        var end=new FootCapsule(body.A+translation,body.B+translation,body.Radius);
        var moved=new MeasuredTriangle(cloth.A+translation,cloth.B+translation,cloth.C+translation);
        Assert.Equal(SweepVerdict.Clear,Check(cloth,moved,body,end));
        Assert.Equal(Check(cloth,cloth,body,body),Check(cloth,moved,body,end));
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public void CapsuleSweepHandlesSlopedSkinnyAndLargeCoordinateFaces(int axis)
    {
        Vector3 Map(Vector3 p)=>(axis==0?p:axis==1?new(p.Y,p.Z,p.X):new(p.Z,p.X,p.Y))+new Vector3(4000,2000,-3000);
        var a=new MeasuredTriangle(Map(new(-1,0,-.002f)),Map(new(0,0,.002f)),Map(new(1,0,-.002f)));
        var high=new FootCapsule(Map(new(0,.3f,0)),Map(new(.1f,.3f,0)),.1f);
        var low=new FootCapsule(Map(new(0,-.3f,0)),Map(new(.1f,-.3f,0)),.1f);
        Assert.Equal(SweepVerdict.Clear,Check(a,a,high,high));Assert.Equal(SweepVerdict.Clear,Check(a,a,low,low));
        Assert.Equal(SweepVerdict.Unproven,Check(a,a,high,low));
    }

    [Fact]
    public void MotionBudgetAndInvalidInputNeverBecomeClear()
    {
        var work=new CollisionWork(0);var body=Pose(1,10,.03f).Capsules[0];
        Assert.Equal(SweepVerdict.BudgetExceeded,CapsuleSweep.Check(Face(Rest),Face(Rest),body,body,.002,ref work));
        Assert.Equal(0,work.Used);
        Assert.Equal(SweepVerdict.Invalid,CapsuleSweep.Check(default,Face(Rest),body,body,.002,ref work));
        var solver=new XpbdCloth(Definition(),new(){Gravity=default,CollisionWorkLimit=0});
        var a=Pose(1,10,.1f);var b=Pose(2,10+1d/120,.1f);var motion=Motion(a,b);
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt).Status);
        Assert.Equal(Rest,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void MissingOrStaleTrackingCannotSilentlyTurnOffAnAcceptedFootGuard()
    {
        var solver=new XpbdCloth(Definition(),new(){Gravity=default});var a=Pose(1,10,.1f);var b=Pose(2,10+1d/120,.1f);var motion=Motion(a,b);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,null,b.SampledAt).Status);
        var c=Pose(3,b.SampledAt+1d/120,.1f);var next=Motion(b,c);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,11).Status);
        Assert.Equal(Rest,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,c.SampledAt).Status);
        solver.Reset();Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty).Status);
    }

    [Fact]
    public void NoSubstepStillSweepsMovingFeetAndCommitsOnlySafeHistory()
    {
        var solver=new XpbdCloth(Definition(),new(){Gravity=default});var a=Pose(1,10,.1f);var b=Pose(2,10+1d/240,.11f);var motion=Motion(a,b);
        var first=solver.AdvanceWithFeet(motion.Duration,MeasuredTriangleScene.Empty,motion,b.SampledAt);
        Assert.Equal(XpbdStatus.Ready,first.Status);Assert.Equal(0,first.Substeps);Assert.Equal(Rest,solver.Capture().Positions.ToArray());
        var c=Pose(3,b.SampledAt+1d/240,.12f);var second=Motion(b,c);
        Assert.Equal(1,solver.AdvanceWithFeet(second.Duration,MeasuredTriangleScene.Empty,second,c.SampledAt).Substeps);
    }

    [Fact]
    public void FailedMotionDoesNotConsumeAcceptedPoseIdentityOrClock()
    {
        var rest=Rest.Select(p=>new Vector3(p.X,.004f,p.Z)).ToArray();var definition=Definition(rest);
        var settings=new XpbdSettings{Gravity=default,Iterations=12};var tested=new XpbdCloth(definition,settings);var control=new XpbdCloth(definition,settings);
        var a=Pose(1,10,.04f);var b=Pose(2,10+1d/240,.04f);var admitted=Motion(a,b);
        Assert.Equal(tested.AdvanceWithFeet(admitted.Duration,Floor(),admitted,b.SampledAt),control.AdvanceWithFeet(admitted.Duration,Floor(),admitted,b.SampledAt));
        var bad=Pose(3,b.SampledAt+1d/60,-.02f);var failure=Motion(b,bad);
        Assert.NotEqual(XpbdStatus.Ready,tested.AdvanceWithFeet(failure.Duration,Floor(),failure,bad.SampledAt).Status);
        var safe=Pose(3,b.SampledAt+1d/240,.04f);var retry=Motion(b,safe);
        var actual=tested.AdvanceWithFeet(retry.Duration,Floor(),retry,safe.SampledAt);var expected=control.AdvanceWithFeet(retry.Duration,Floor(),retry,safe.SampledAt);
        Assert.Equal(expected,actual);Assert.Equal(1,actual.Substeps);Assert.Equal(control.Capture().Positions.ToArray(),tested.Capture().Positions.ToArray());
    }

    [Fact]
    public void NearBoundaryBroadphaseDoesNotDiscardCapsuleContact()
    {
        var body=new FootCapsule(new(0,.222f,0),new(.1f,.222f,0),.22f);var cloth=new MeasuredTriangle(new(-1,0,-1),new(0,0,1),new(1,0,-1));
        var work=new CollisionWork(1000);
        var direct=CapsuleSweep.Check(cloth,cloth,body,body,.002f,ref work);work=new(1000);
        var pruned=FootCapsuleContacts.Sweep([cloth.A,cloth.B,cloth.C],[cloth.A,cloth.B,cloth.C],[0,1,2],[body],[body],.002f,ref work);
        Assert.Equal(direct,pruned);Assert.NotEqual(SweepVerdict.Invalid,pruned);
    }

    [Fact]
    public void SuccessfulObservationClockCannotMoveBackwardEvenWithIncreasingSampleSequence()
    {
        var solver=new XpbdCloth(Definition(),new(){Gravity=default});
        var a=Pose(1,10,.1f);var b=Pose(2,10+1d/120,.1f);var first=Motion(a,b);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(first.Duration,MeasuredTriangleScene.Empty,first,10.02).Status);
        var c=Pose(3,10+1d/60,.1f);var next=Motion(b,c);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,c.SampledAt).Status);
        Assert.Equal(Rest,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(next.Duration,MeasuredTriangleScene.Empty,next,10.02).Status);
        solver.Reset();Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(first.Duration,MeasuredTriangleScene.Empty,first,b.SampledAt).Status);
    }

    [Fact]
    public void SameSequenceCloneIsNotTheActuallyAcceptedObservation()
    {
        var solver=new XpbdCloth(Definition(),new(){Gravity=default});var a=Pose(1,10,.1f);var b=Pose(2,10+1d/120,.1f);var first=Motion(a,b);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(first.Duration,MeasuredTriangleScene.Empty,first,b.SampledAt).Status);
        var clone=Pose(2,b.SampledAt,.1f);var next=Pose(3,b.SampledAt+1d/120,.1f);var disconnected=Motion(clone,next);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.AdvanceWithFeet(disconnected.Duration,MeasuredTriangleScene.Empty,disconnected,next.SampledAt).Status);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(next.SampledAt-b.SampledAt,MeasuredTriangleScene.Empty,Motion(b,next),next.SampledAt).Status);
    }

    [Fact]
    public void FiniteCapsuleDoesNotBlockAnUnrelatedLayerAboveOrBelowIt()
    {
        var body=Pose(1,10,.1f).Capsules[0];
        foreach(var height in new[]{-.5f,.5f})
        {
            var face=Face(Rest.Select(p=>new Vector3(p.X,height,p.Z)).ToArray());
            Assert.Equal(SweepVerdict.Clear,Check(face,face,body,body));
        }
    }

    [Fact]
    public void LocalSheetWithTwoMeasuredSizeCapsulesHasCountedBoundedWorkAndNoSolverAllocation()
    {
        const int size=9;var points=new Vector3[size*size];var faces=new List<int>();var edges=new List<DistanceEdge>();
        for(var z=0;z<size;z++)for(var x=0;x<size;x++)
        {
            var i=z*size+x;points[i]=new((x-4)*.08f,.004f,(z-4)*.08f);
            if(x<size-1)edges.Add(new(i,i+1,0));if(z<size-1)edges.Add(new(i,i+size,0));
            if(x<size-1&&z<size-1){faces.AddRange([i,i+size,i+1,i+1,i+size,i+size+1]);edges.Add(new(i,i+size+1,MaterialEdge.Shear));edges.Add(new(i+1,i+size,MaterialEdge.Shear));}
        }
        var definition=new XpbdDefinition(points,new Vector2[points.Length],faces.ToArray(),Enumerable.Repeat(1f,points.Length).ToArray(),edges.ToArray());
        var solver=new XpbdCloth(definition);var floor=Floor();var motion=new FootProxyMotion[38];
        FootProxyPose Create(int sequence)
        {
            FootMarkerEnvelope[] markers=[new(new(-.1f,-.14f),.014f,.22f),new(new(.1f,-.14f),.014f,.22f),
                new(new(-.1f,.14f),.014f,.22f),new(new(.1f,.14f),.014f,.22f)];
            Assert.True(FootProxyPose.TryCreate(Identity,sequence,10+(sequence-1)/60d,markers,out var pose));return pose!;
        }
        var previous=Create(1);
        for(var i=0;i<motion.Length;i++){var next=Create(i+2);motion[i]=Motion(previous,next);previous=next;}
        for(var i=0;i<8;i++)Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(motion[i].Duration,floor,motion[i],motion[i].After.SampledAt).Status);
        var watch=System.Diagnostics.Stopwatch.StartNew();var allocated=GC.GetAllocatedBytesForCurrentThread();var ready=true;XpbdAdvance result=default;
        for(var i=8;i<motion.Length;i++){result=solver.AdvanceWithFeet(motion[i].Duration,floor,motion[i],motion[i].After.SampledAt);ready&=result.Status==XpbdStatus.Ready;}
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;watch.Stop();
        Assert.True(ready);Assert.Equal(0,allocated);Assert.True(result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes<=100_000);
        output.WriteLine($"81 particles / 128 cloth faces / 2 floor triangles / 2 radius.22 capsules: {watch.Elapsed.TotalMilliseconds/30:F3}ms per60Hz; pairs={result.TrianglePairs}; sweep={result.SweepNodes}; tree={result.BroadphaseNodes}; allocations={allocated} (snapshot creation excluded).");
    }
}
