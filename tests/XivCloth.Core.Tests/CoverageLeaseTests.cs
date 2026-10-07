using System.Numerics;

namespace XivCloth.Core.Tests;

public sealed class CoverageLeaseTests
{
    private static readonly CollisionCoverage Bounds = new(new(-1), new(1));
    private static readonly MeasuredTriangle Face = new(new(-2, -.5f, -2), new(-2, -.5f, 2), new(2, -.5f, -2));
    private static XpbdDefinition Definition(float x = 0) => new(
        [new(x-.1f, .2f, -.1f), new(x, .2f, .1f), new(x+.1f, .2f, -.1f)],
        [new(0,0), new(.5f,1), new(1,0)], [0,1,2], [1,1,1],
        [new(0,1,0), new(1,2,0), new(2,0,0)]);
    private static FootProxyPose Feet(long sequence,double now)
    {
        FootMarkerEnvelope[] markers = [new(new(-.1f,0),.8f,.1f),new(new(.1f,0),.8f,.1f),
            new(new(-.1f,.4f),.8f,.1f),new(new(.1f,.4f),.8f,.1f)];
        Assert.True(FootProxyPose.TryCreate(new(1,2,3,4),sequence,now,markers,out var feet));return feet!;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BoundsApplyBeforeEmptySceneAndBeforeCachedCandidateReuse(bool empty)
    {
        var scene = new MeasuredTriangleScene(1,empty?[]:[Face],twoSided:false,coverage:Bounds);
        var cache = new FaceQueryCache(1);var work=new CollisionWork(1000);Span<int> ids=stackalloc int[128];
        Assert.True(cache.Query(0,scene,new(new(-.1f),new(.1f)),ids,ref work)>=0);
        Assert.Equal(-2,cache.Query(0,scene,new(new(.99f),new(1.01f)),ids,ref work));
        // Actual box is inside, but a fresh cache's .025 padding is outside.
        if(!empty)Assert.Equal(-2,cache.Query(0,scene,new(new(.98f),new(.99f)),ids,ref work));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WholeFaceAndSweptVolumeCannotLeaveCaptureEvenForEmptyOrThinScene(bool twoSided)
    {
        var scene=new MeasuredTriangleScene(1,[],twoSided,Bounds);
        Vector3[] before=[new(-.1f,0,-.1f),new(0,0,.1f),new(.1f,0,-.1f)];
        Vector3[] after=[new(-.1f,0,-.1f),new(1.1f,0,.1f),new(.1f,0,-.1f)];
        var work=new CollisionWork(1000);
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.OneSidedClear(after,[0,1,2],scene,.002f,ref work));
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.Sweep(before,after,[0,1,2],scene,.002f,ref work));
        var p=new Vector3(.999f,0,0);var checks=0;
        Assert.False(scene.IsClear(p,.004f,ref checks));Assert.False(scene.Project(ref p,.004f,.05f,ref checks));
        Assert.Equal(float.PositiveInfinity,scene.MaximumVertexPenetration(after));
    }
    [Fact]
    public void CoverageRefusalRollsBackCompleteCallRatherThanCroppingOrClamping()
    {
        var definition=Definition(.85f);var solver=new XpbdCloth(definition,new(){Gravity=Vector3.Zero});
        var scene=new MeasuredTriangleScene(1,[],twoSided:false,coverage:Bounds);
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(scene).Status);
        var before=solver.Capture();
        var force=new XpbdStepInputs(definition,[new(30,0,0),new(30,0,0),new(30,0,0)]);
        var result=solver.Advance(1d/60,scene,force);
        Assert.NotEqual(XpbdStatus.Ready,result.Status);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(before.Indices.ToArray(),solver.Capture().Indices.ToArray());
        Assert.Equal(1,solver.SceneGeneration);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CommitFalseOrThrowPreservesPoseVelocityClockAcceptedFeetAndRetry(bool throws)
    {
        var definition=Definition();var solver=new XpbdCloth(definition);var control=new XpbdCloth(definition);
        var scene=new MeasuredTriangleScene(1,[],twoSided:false,coverage:Bounds);
        var a=Feet(1,1);var b=Feet(2,1.01);var c=Feet(3,1.02);var d=Feet(4,1.03);
        Assert.True(MeasuredFootInterval.TryCreate(a,b,1.01,out var first));
        Assert.True(MeasuredFootInterval.TryCreate(b,c,1.02,out var next));
        Assert.True(MeasuredFootInterval.TryCreate(c,d,1.03,out var last));
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeet(scene,first,1.01).Status);
        control.AdvanceMeasuredFeet(scene,first,1.01);var before=solver.Capture();
        Assert.Equal(XpbdStatus.RejectedInput,solver.AdvanceMeasuredFeet(scene,next,1.02,
            inputs:null,canCommit:()=>throws?throw new InvalidOperationException():false).Status);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForFeet(b,b,1.02).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForFeet(c,c,1.02).Status);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeet(scene,next,1.02).Status);
        control.AdvanceMeasuredFeet(scene,next,1.02);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeet(scene,last,1.03).Status);
        control.AdvanceMeasuredFeet(scene,last,1.03);
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(control.SceneGeneration,solver.SceneGeneration);
    }
    [Fact]
    public void ReentrantMutationCannotEscapeCommitGuardAndRebindFalsePreservesGeneration()
    {
        var solver=new XpbdCloth(Definition());var scene=new MeasuredTriangleScene(1,[],twoSided:false,coverage:Bounds);
        solver.RebindScene(scene);var before=solver.Capture();
        Assert.Equal(XpbdStatus.RejectedInput,solver.RebindScene(new(2,[],twoSided:false,coverage:Bounds),()=>false).Status);
        Assert.Equal(1,solver.SceneGeneration);
        Assert.True(MeasuredFootInterval.TryCreate(Feet(1,1),Feet(2,1.01),1.01,out var interval));
        Assert.Equal(XpbdStatus.RejectedInput,solver.AdvanceMeasuredFeet(scene,interval,1.01,
            inputs:null,canCommit:()=>{Assert.Equal(XpbdStatus.RejectedInput,solver.Advance(.01,scene).Status);solver.Reset();return true;}).Status);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(1,solver.SceneGeneration);
    }
    [Fact]
    public void CommitPredicateCannotExportUncommittedPose()
    {
        var solver=new XpbdCloth(Definition());var scene=new MeasuredTriangleScene(1,[],twoSided:false,coverage:Bounds);
        var a=Feet(1,1);var b=Feet(2,1.01);var c=Feet(3,1.02);
        Assert.True(MeasuredFootInterval.TryCreate(a,b,1.01,out var first));
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeet(scene,first,1.01).Status);
        Assert.True(MeasuredFootInterval.TryCreate(b,c,1.02,out var next));var before=solver.Capture();
        Assert.Equal(XpbdStatus.RejectedInput,solver.AdvanceMeasuredFeet(scene,next,1.02,inputs:null,canCommit:()=>
        {
            Assert.Equal(XpbdStatus.RejectedInput,solver.CaptureForFeet(b,b,1.02).Status);
            solver.Capture();return true;
        }).Status);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
    }
    [Fact]
    public void ExistingClrSignaturesAndOptionalSourceDefaultsArePreserved()
    {
        var originalConstructor=typeof(MeasuredTriangleScene).GetConstructor(
            [typeof(long),typeof(ReadOnlySpan<MeasuredTriangle>),typeof(bool)]);
        Assert.NotNull(originalConstructor);
        Assert.True(originalConstructor.GetParameters()[2].IsOptional);
        Assert.NotNull(typeof(XpbdCloth).GetMethod(nameof(XpbdCloth.RebindScene),[typeof(MeasuredTriangleScene)]));
        var originalAdvance=typeof(XpbdCloth).GetMethod(nameof(XpbdCloth.AdvanceMeasuredFeet),
            [typeof(MeasuredTriangleScene),typeof(MeasuredFootInterval),typeof(double),typeof(XpbdStepInputs)]);
        Assert.NotNull(originalAdvance);Assert.True(originalAdvance.GetParameters()[3].IsOptional);
        var extendedAdvance=typeof(XpbdCloth).GetMethod(nameof(XpbdCloth.AdvanceMeasuredFeet),
            [typeof(MeasuredTriangleScene),typeof(MeasuredFootInterval),typeof(double),typeof(XpbdStepInputs),typeof(Func<bool>)]);
        Assert.NotNull(extendedAdvance);Assert.All(extendedAdvance.GetParameters(),p=>Assert.False(p.IsOptional));
        var originalScene=new MeasuredTriangleScene(1,[]);
        Assert.Null(originalScene.Coverage);
        var solver=new XpbdCloth(Definition());Assert.Equal(XpbdStatus.Ready,solver.RebindScene(originalScene).Status);
        Assert.True(MeasuredFootInterval.TryCreate(Feet(1,1),Feet(2,1.01),1.01,out var interval));
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeet(originalScene,interval,1.01).Status);
    }
}
