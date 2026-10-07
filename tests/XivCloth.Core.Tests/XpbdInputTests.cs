using System.Numerics;
using XivCloth.Core;

public sealed class XpbdInputTests(ITestOutputHelper output)
{
    private static readonly Vector3[] Triangle=[new(-.1f,.3f,-.1f),new(0,.3f,.1f),new(.1f,.3f,-.1f)];
    private static XpbdDefinition Definition(Vector3[]? positions=null,bool isolatedFirst=false,bool pinFirst=false)
    {
        var p=positions??Triangle;
        return new(p,new Vector2[3],[0,1,2],isolatedFirst?[1,0,0]:pinFirst?[0,1,1]:[1,1,1],
            isolatedFirst?[new(1,2,0)]:[new(0,1,0),new(1,2,0),new(2,0,0)]);
    }
    private static (XpbdDefinition Definition,Vector3[] Positions) Sheet(int n=5)
    {
        var p=new Vector3[n*n];var edges=new List<DistanceEdge>();var faces=new List<int>();
        for(var z=0;z<n;z++)for(var x=0;x<n;x++)
        {
            var i=z*n+x;p[i]=new((x-(n-1)*.5f)*.1f,.3f,(z-(n-1)*.5f)*.1f);
            if(x<n-1)edges.Add(new(i,i+1,0));if(z<n-1)edges.Add(new(i,i+n,0));
            if(x<n-1&&z<n-1){faces.AddRange([i,i+n,i+1,i+1,i+n,i+n+1]);edges.Add(new(i,i+n+1,MaterialEdge.Shear));edges.Add(new(i+1,i+n,MaterialEdge.Shear));}
        }
        return (new(p,new Vector2[p.Length],faces.ToArray(),Enumerable.Repeat(1f,p.Length).ToArray(),edges.ToArray()),p);
    }
    [Fact]
    public void ConstantAccelerationIsExactlyIndependentOfCallerGrouping()
    {
        var definition=Definition();var inputs=new XpbdStepInputs(definition,Enumerable.Repeat(new Vector3(2,1,-3),3).ToArray());
        var settings=new XpbdSettings{Gravity=default,Drag=1};var a=new XpbdCloth(definition,settings);var b=new XpbdCloth(definition,settings);
        for(var i=0;i<120;i++)Assert.Equal(XpbdStatus.Ready,a.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,inputs).Status);
        for(var i=0;i<60;i++)Assert.Equal(XpbdStatus.Ready,b.Advance(1d/60,MeasuredTriangleScene.Empty,inputs).Status);
        Assert.Equal(a.Capture().Positions.ToArray(),b.Capture().Positions.ToArray());
        var displacement=a.Capture().Positions[0]-Triangle[0];Assert.True(displacement.X>.5f&&displacement.Y>.2f&&displacement.Z<-.8f);
    }
    [Fact]
    public void ConstantTargetsAndAccelerationTogetherRemainGroupingInvariant()
    {
        var definition=Definition(isolatedFirst:true);var settings=new XpbdSettings{Gravity=default,Drag=3};
        var inputs=new XpbdStepInputs(definition,[new(0,2,0),default,default],[new(0,Triangle[0]+new Vector3(.1f),.001f)]);
        var a=new XpbdCloth(definition,settings);var b=new XpbdCloth(definition,settings);
        for(var i=0;i<120;i++)Assert.Equal(XpbdStatus.Ready,a.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,inputs).Status);
        for(var i=0;i<60;i++)Assert.Equal(XpbdStatus.Ready,b.Advance(1d/60,MeasuredTriangleScene.Empty,inputs).Status);
        Assert.Equal(a.Capture().Positions.ToArray(),b.Capture().Positions.ToArray());
    }
    [Fact]
    public void ZeroAccelerationAndEmptyTargetsPreserveBaselineExactly()
    {
        var grid=Sheet();var a=new XpbdCloth(grid.Definition);var b=new XpbdCloth(grid.Definition);
        var zeros=new XpbdStepInputs(grid.Definition,new Vector3[grid.Positions.Length]);
        for(var i=0;i<60;i++)
        {
            Assert.Equal(XpbdStatus.Ready,a.Advance(1d/60,MeasuredTriangleScene.Empty).Status);
            Assert.Equal(XpbdStatus.Ready,b.Advance(1d/60,MeasuredTriangleScene.Empty,zeros).Status);
        }
        Assert.Equal(a.Capture().Positions.ToArray(),b.Capture().Positions.ToArray());
    }
    [Fact]
    public void SnapshotCopiesInputsAndIsBoundToExactDefinitionIdentity()
    {
        var definition=Definition();Vector3[] acceleration=[new(1,0,0),default,default];
        XpbdTarget[] targets=[new(0,Triangle[0]+new Vector3(.01f,0,0),.001f)];
        var snapshot=new XpbdStepInputs(definition,acceleration,targets);
        acceleration[0]=new(20);targets[0]=new(1,new(5),1);
        Assert.Equal(new Vector3(1,0,0),snapshot.Accelerations[0]);Assert.Equal(0,snapshot.Targets[0].Particle);
        var other=new XpbdCloth(Definition());
        Assert.Throws<ArgumentException>(()=>other.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,snapshot));
        Assert.Equal(Triangle,other.Capture().Positions.ToArray());
    }
    [Fact]
    public void FractionalNoStepSnapshotIsDiscardedNotLatched()
    {
        var definition=Definition();var solver=new XpbdCloth(definition,new(){Gravity=default});
        var inputs=new XpbdStepInputs(definition,Enumerable.Repeat(new Vector3(30,0,0),3).ToArray(),[new(0,Triangle[0]+new Vector3(.1f),0)]);
        var first=solver.Advance(XpbdCloth.FixedStep/2,MeasuredTriangleScene.Empty,inputs);
        Assert.Equal(0,first.Substeps);Assert.Equal(0,first.InputWork);
        var second=solver.Advance(XpbdCloth.FixedStep/2,MeasuredTriangleScene.Empty);
        Assert.Equal(1,second.Substeps);Assert.Equal(0,second.InputWork);Assert.Equal(Triangle,solver.Capture().Positions.ToArray());
    }
    [Theory]
    [InlineData(1)][InlineData(4)][InlineData(16)]
    public void XyzTargetUsesAccumulatedXpbdLambdaNotRepeatedLerp(int iterations)
    {
        var definition=Definition(isolatedFirst:true);var settings=new XpbdSettings{Gravity=default,Drag=0,Iterations=iterations};
        var solver=new XpbdCloth(definition,settings);var offset=new Vector3(.01f,.008f,-.012f);const float compliance=.0001f;
        var inputs=new XpbdStepInputs(definition,targets:[new(0,Triangle[0]+offset,compliance)]);
        var result=solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,inputs);
        Assert.Equal(XpbdStatus.Ready,result.Status);Assert.Equal(iterations*3,result.InputWork);
        var alpha=compliance/(XpbdCloth.FixedStep*XpbdCloth.FixedStep);var expected=Triangle[0]+offset/(float)(1+alpha);
        Assert.InRange(Vector3.Distance(expected,solver.Capture().Positions[0]),0,.000001f);
    }
    [Fact]
    public void TargetCorrectionTravelIsCappedAcrossAllIterationsOfASubstep()
    {
        var definition=Definition(isolatedFirst:true);var solver=new XpbdCloth(definition,new(){Gravity=default,Drag=0,Iterations=16});
        var inputs=new XpbdStepInputs(definition,targets:[new(0,Triangle[0]+Vector3.UnitX,0)]);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,inputs).Status);
        Assert.InRange(Vector3.Distance(Triangle[0],solver.Capture().Positions[0]),.039999f,.040001f);
        // Releasing a target leaves actual physical velocity; it does not reset
        // the actor or pin it to the last target position.
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty).Status);
        Assert.InRange(solver.Capture().Positions[0].X-Triangle[0].X,.07999f,.08001f);
    }
    [Fact]
    public void SmoothMovingCornerPullProducesRealMaterialLagAndThreeDimensionalTravel()
    {
        var grid=Sheet();var solver=new XpbdCloth(grid.Definition,new(){Gravity=default,Drag=3,StretchCompliance=.00001f,ShearCompliance=.0001f});
        var prior=grid.Positions[0];var maximumMove=0f;
        for(var i=1;i<=48;i++)
        {
            var target=grid.Positions[0]+new Vector3(-.25f,.12f,.08f)*(i/48f);
            var input=new XpbdStepInputs(grid.Definition,targets:[new(0,target,.0001f)]);
            Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,input).Status);
            var next=solver.Capture().Positions[0];maximumMove=Math.Max(maximumMove,Vector3.Distance(next,prior));prior=next;
        }
        var frame=solver.Capture();var lead=frame.Positions[0]-grid.Positions[0];var far=frame.Positions[^1]-grid.Positions[^1];
        var strain=0f;
        foreach(var edge in grid.Definition.Edges)
            strain=Math.Max(strain,Math.Abs(Vector3.Distance(frame.Positions[edge.A],frame.Positions[edge.B])/edge.Length-1));
        output.WriteLine($"moving pull: lead={lead}, far={far}, differential travel={Vector3.Distance(lead,far):R}, max edge strain={strain:R}, maximum per-substep lead movement={maximumMove:R}");
        Assert.True(lead.X<-.15f&&lead.Y>.05f&&lead.Z>.025f);
        Assert.True(Vector3.Distance(lead,far)>.04f);Assert.InRange(maximumMove,0,.04f);
        Assert.InRange(strain,.001f,.3f); // Not merely a rigid transform.
        Assert.True(Vector3.Distance(frame.Positions[0],grid.Positions[0]+new Vector3(-.25f,.12f,.08f))>.001f);
    }
    [Fact]
    public void AccelerationAndConflictingTargetNeverMovePinnedParticle()
    {
        var definition=Definition(pinFirst:true);var solver=new XpbdCloth(definition,new(){Gravity=default});
        var inputs=new XpbdStepInputs(definition,[new(30,0,0),default,default],[new(0,new(9999),0)]);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(1d/60,MeasuredTriangleScene.Empty,inputs).Status);
        Assert.Equal(Triangle,solver.Capture().Positions.ToArray());
    }
    [Fact]
    public void SpatialAccelerationAppliesOnlyToItsParticleNotARigidSheetTranslation()
    {
        var definition=Definition(isolatedFirst:true);var solver=new XpbdCloth(definition,new(){Gravity=default,Drag=0});
        var input=new XpbdStepInputs(definition,[new(0,4,0),default,default]);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,input).Status);
        Assert.InRange(solver.Capture().Positions[0].Y-Triangle[0].Y,4f/14400-.000001f,4f/14400+.000001f);
        Assert.Equal(Triangle[1],solver.Capture().Positions[1]);Assert.Equal(Triangle[2],solver.Capture().Positions[2]);
    }
    [Fact]
    public void PullAgainstMeasuredWallStopsAtContactInsteadOfFollowingTheTargetThroughIt()
    {
        Vector3[] rest=[new(-.02f,-.1f,-.1f),new(-.02f,0,.1f),new(-.02f,.1f,-.1f)];var definition=Definition(rest);
        var solver=new XpbdCloth(definition,new(){Gravity=default,Drag=0,Iterations=4});
        var input=new XpbdStepInputs(definition,targets:Enumerable.Range(0,3).Select(i=>new XpbdTarget(i,rest[i]+new Vector3(.12f,0,0),.001f)).ToArray());
        var scene=new MeasuredTriangleScene(1,[new(new(0,-2,-2),new(0,2,2),new(0,2,-2)),new(new(0,-2,-2),new(0,-2,2),new(0,2,2))]);
        for(var i=0;i<30;i++)Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,scene,input).Status);
        foreach(var p in solver.Capture().Positions)Assert.InRange(p.X,-.0041f,-.00399f);
        Assert.False(solver.Capture().EndpointInterpolationAllowed);
    }
    [Fact]
    public void AggressiveTargetPlusExistingVelocityCanExceedContactResponseBudgetAndMustRefuse()
    {
        Vector3[] rest=[new(-.02f,-.1f,-.1f),new(-.02f,0,.1f),new(-.02f,.1f,-.1f)];var definition=Definition(rest);
        var solver=new XpbdCloth(definition,new(){Gravity=default,Drag=0,Iterations=4});
        var input=new XpbdStepInputs(definition,targets:Enumerable.Range(0,3).Select(i=>new XpbdTarget(i,rest[i]+new Vector3(.12f,0,0),0)).ToArray());
        var scene=new MeasuredTriangleScene(1,[new(new(0,-2,-2),new(0,2,2),new(0,2,-2)),new(new(0,-2,-2),new(0,-2,2),new(0,2,2))]);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,scene,input).Status);
        var before=solver.Capture().Positions.ToArray();
        // The .04 correction cap is NOT a total displacement/speed guarantee;
        // prediction plus target correction can exceed the existing .05 contact
        // correction budget. Preserve the refusal instead of weakening it.
        Assert.Equal(XpbdStatus.InvalidContactOrState,solver.Advance(XpbdCloth.FixedStep,scene,input).Status);
        Assert.Equal(before,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,scene).Status);
        foreach(var p in solver.Capture().Positions)Assert.True(p.X<0);
    }
    [Fact]
    public void ResetAfterForcesAndTargetsClearsNewScratchAndRetainedMotion()
    {
        var definition=Definition(isolatedFirst:true);var settings=new XpbdSettings{Gravity=default};
        var solver=new XpbdCloth(definition,settings);var fresh=new XpbdCloth(definition,settings);
        var input=new XpbdStepInputs(definition,[new(0,3,0),default,default],[new(0,Triangle[0]+new Vector3(.1f),.001f)]);
        for(var i=0;i<5;i++)Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,input).Status);
        solver.Reset();
        Assert.Equal(fresh.Advance(1d/60,MeasuredTriangleScene.Empty),solver.Advance(1d/60,MeasuredTriangleScene.Empty));
        Assert.Equal(fresh.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
    }
    [Fact]
    public void FarNonPinnedTargetIsRefusedBeforeAnyClockOrPhysicsChange()
    {
        var definition=Definition();var solver=new XpbdCloth(definition);var control=new XpbdCloth(definition);
        var inputs=new XpbdStepInputs(definition,targets:[new(0,Triangle[0]+new Vector3(1.001f,0,0),0)]);
        Assert.Equal(XpbdStatus.RejectedInput,solver.Advance(.5,MeasuredTriangleScene.Empty,inputs).Status);
        Assert.Equal(Triangle,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
        Assert.Equal(control.Advance(1d/60,MeasuredTriangleScene.Empty),solver.Advance(1d/60,MeasuredTriangleScene.Empty));
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
    }
    [Fact]
    public void InputWorkBudgetRefusesBeforeStartingAnySubstep()
    {
        var grid=Sheet(9);var solver=new XpbdCloth(grid.Definition,new(){Iterations=16});
        var targets=Enumerable.Range(0,64).Select(i=>new XpbdTarget(i,grid.Positions[i],.001f)).ToArray();
        var inputs=new XpbdStepInputs(grid.Definition,targets:targets);
        var result=solver.Advance(1d/15,MeasuredTriangleScene.Empty,inputs);
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,result.Status);Assert.Equal(0,result.InputWork);Assert.Equal(0,result.ConstraintSolves);
        Assert.Equal(grid.Positions,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
    }
    [Fact]
    public void TargetDrivenLaterSubstepCollisionRestoresMotionClockAndMultiplierState()
    {
        Vector3[] rest=[new(-.08f,-1,-1),new(-.08f,0,1),new(-.08f,1,-1)];var definition=Definition(rest);
        var settings=new XpbdSettings{Gravity=default,Drag=0,Iterations=1};
        var input=new XpbdStepInputs(definition,targets:Enumerable.Range(0,3).Select(i=>new XpbdTarget(i,rest[i]+new Vector3(.16f,0,0),0)).ToArray());
        var wall=new MeasuredTriangle(new(0,-.05f,-.05f),new(0,0,.05f),new(0,.05f,-.05f));
        var scene=new MeasuredTriangleScene(1,[wall],true);var solver=new XpbdCloth(definition,settings);var control=new XpbdCloth(definition,settings);
        solver.Advance(XpbdCloth.FixedStep/2,scene);control.Advance(XpbdCloth.FixedStep/2,scene);
        var failed=solver.Advance(2*XpbdCloth.FixedStep,scene,input);
        Assert.Equal(XpbdStatus.CollisionUnproven,failed.Status);Assert.Equal(0,failed.Substeps);Assert.Equal(18,failed.InputWork);
        Assert.Equal(rest,solver.Capture().Positions.ToArray());
        var retried=solver.Advance(XpbdCloth.FixedStep/2,scene,input);var expected=control.Advance(XpbdCloth.FixedStep/2,scene,input);
        Assert.Equal(expected,retried);Assert.Equal(1,retried.Substeps);Assert.Equal(XpbdStatus.Ready,retried.Status);
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
        // New call with no target cannot inherit a failed call's lambda/target.
        Assert.Equal(control.Advance(XpbdCloth.FixedStep,scene),solver.Advance(XpbdCloth.FixedStep,scene));
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
    }
    [Fact]
    public void AccelerationAndGravityRemainWithinExistingVelocityCap()
    {
        var definition=Definition();var settings=new XpbdSettings{Gravity=new(0,30,0),Drag=0,MaximumVelocity=5};
        var solver=new XpbdCloth(definition,settings);var input=new XpbdStepInputs(definition,Enumerable.Repeat(new Vector3(0,30,0),3).ToArray());
        var prior=Triangle[0];
        for(var i=0;i<120;i++)
        {
            Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,input).Status);
            var next=solver.Capture().Positions[0];Assert.InRange(next.Y-prior.Y,0,5f/120+.000001f);prior=next;
        }
    }
    [Fact]
    public void MalformedSnapshotsFailBeforeTheyCanEnterASolver()
    {
        var d=Definition();
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,new Vector3[2]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,[new(float.NaN),default,default]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,[new(30.001f,0,0),default,default]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,targets:[new(-1,default,0)]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,targets:[new(3,default,0)]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,targets:[new(0,default,0),new(0,default,0)]));
        foreach(var c in new[]{-.01f,1.001f,float.NaN,float.PositiveInfinity})
            Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,targets:[new(0,default,c)]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,targets:[new(0,new(float.PositiveInfinity),0)]));
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(d,targets:[new(0,new(10001),0)]));
        var grid=Sheet(9);
        Assert.Throws<ArgumentException>(()=>new XpbdStepInputs(grid.Definition,targets:Enumerable.Range(0,65).Select(i=>new XpbdTarget(i,default,0)).ToArray()));
    }
    [Fact]
    public void ReusedImmutableInputAddsNoPerSubstepManagedAllocations()
    {
        var definition=Definition();var solver=new XpbdCloth(definition,new(){Gravity=default});
        var input=new XpbdStepInputs(definition,new Vector3[3],[new(0,Triangle[0],.001f)]);
        for(var i=0;i<30;i++)solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,input);
        var before=GC.GetAllocatedBytesForCurrentThread();var ready=true;
        for(var i=0;i<100;i++)ready&=solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty,input).Status==XpbdStatus.Ready;
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(ready);Assert.Equal(0,allocated);
    }
}
