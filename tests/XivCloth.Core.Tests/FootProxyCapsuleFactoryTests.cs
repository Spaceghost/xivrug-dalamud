using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class FootProxyCapsuleFactoryTests
{
    private static readonly FootProxyIdentity Identity=new(1,2,3,4);
    private static FootCapsule[] Capsules()=>[new(new(-.2f,.5f,-.1f),new(-.16f,.58f,.1f),.22f),
        new(new(.2f,.6f,-.1f),new(.15f,.68f,.1f),.21f)];
    private static FootProxyModelBinding Model(object source)
    {Assert.True(FootProxyModelBinding.TryCreate(Identity,source,out var model));return model!;}
    private static FootProxyPose Pose(FootProxyModelBinding model,object source,long sequence=1,double time=1)
    {Assert.True(FootProxyPose.TryCreateCapsules(model,source,new object(),sequence,time,Capsules(),out var pose));return pose!;}

    [Fact] public void PreservesExactRotatedAxesAndOwnsSourceArray()
    {
        var source=new object();var capture=new object();var model=Model(source);var capsules=Capsules();var expected=capsules.ToArray();
        Assert.True(FootProxyPose.TryCreateCapsules(model,source,capture,9,10,capsules,out var pose));
        capsules[0]=default;
        Assert.Equal(expected,pose!.Capsules.ToArray());Assert.NotEqual(pose.Capsules[0].A.Y,pose.Capsules[0].B.Y);
        Assert.Same(capture,pose.CaptureBinding);Assert.Same(model,pose.Identity.ModelBinding);
        Assert.Equal(9,pose.Sequence);Assert.Equal(10,pose.SampledAt);
        var (zone,actor,instance,generation)=pose.Identity;
        Assert.Equal((1u,2ul,3ul,4L),(zone,actor,instance,generation));
    }

    [Fact] public void ExactModelReferenceNotArbitraryEqualsControlsBinding()
    {
        var source=new EqualToEverything();var other=new EqualToEverything();var model=Model(source);
        Assert.True(source.Equals(other));
        Assert.False(FootProxyPose.TryCreateCapsules(model,other,new object(),1,1,Capsules(),out var pose));Assert.Null(pose);
        Assert.False(model.Equals(Model(source)));
        Assert.NotEqual(Pose(model,source).Identity,Pose(Model(other),other).Identity);
    }

    [Fact] public void GenuineNextCaptureChangesMetadataButNotModelIdentity()
    {
        var source=new object();var model=Model(source);var before=Pose(model,source);var after=Pose(model,source,2,1.01);
        Assert.NotSame(before.CaptureBinding,after.CaptureBinding);Assert.Equal(before.Identity,after.Identity);
        Assert.True(FootProxyMotion.TryCreate(before,after,1.011,out _));
    }

    [Fact] public void ReplacedCalibrationOrBindingRejectsMotionEvenWithIdenticalGeometry()
    {
        var source=new object();var before=Pose(Model(source),source);
        var replacement=Pose(Model(source),source,2,1.01);
        Assert.False(FootProxyMotion.TryCreate(before,replacement,1.011,out _));
    }

    [Fact] public void BoundIdentityCannotBeUsedToBuildLegacyOrAnotherModel()
    {
        var source=new object();var pose=Pose(Model(source),source);
        FootMarkerEnvelope[] markers=[new(new(-.2f,-.1f),.2f,.2f),new(new(-.2f,.1f),.2f,.2f),
            new(new(.2f,-.1f),.2f,.2f),new(new(.2f,.1f),.2f,.2f)];
        Assert.False(FootProxyPose.TryCreate(pose.Identity,2,1.01,markers,out _));
        Assert.False(FootProxyModelBinding.TryCreate(pose.Identity,source,out _));
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void ExactlyTwoCapsulesRequired(int count)
    {
        var source=new object();var values=Enumerable.Repeat(Capsules()[0],count).ToArray();
        Assert.False(FootProxyPose.TryCreateCapsules(Model(source),source,new object(),1,1,values,out _));
    }

    [Fact] public void InvalidBoundsAreRefusedNotClamped()
    {
        var source=new object();var model=Model(source);var good=Capsules()[0];
        foreach(var invalid in new[]{good with{Radius=.019f},good with{Radius=.341f},good with{Radius=float.NaN},
            good with{A=new(float.NaN,0,0)},good with{B=new(0,float.PositiveInfinity,0)},
            good with{A=new(10001,0,0),B=new(10001,.1f,0)},good with{B=good.A+Vector3.UnitX*.751f}})
            Assert.False(FootProxyPose.TryCreateCapsules(model,source,new object(),1,1,[invalid,good],out _));
        Assert.True(FootProxyPose.TryCreateCapsules(model,source,new object(),1,1,
            [new(Vector3.Zero,Vector3.UnitX*.75f,.34f),new(Vector3.One,Vector3.One,.02f)],out _));
    }

    [Fact] public void MissingIdentitySourceCaptureSequenceOrClockRefuses()
    {
        var source=new object();var model=Model(source);
        Assert.False(FootProxyModelBinding.TryCreate(default,source,out _));
        Assert.False(FootProxyModelBinding.TryCreate(Identity,null,out _));
        Assert.False(FootProxyPose.TryCreateCapsules(null,source,new object(),1,1,Capsules(),out _));
        Assert.False(FootProxyPose.TryCreateCapsules(model,null,new object(),1,1,Capsules(),out _));
        Assert.False(FootProxyPose.TryCreateCapsules(model,source,null,1,1,Capsules(),out _));
        Assert.False(FootProxyPose.TryCreateCapsules(model,source,new object(),0,1,Capsules(),out _));
        foreach(var time in new[]{double.NaN,double.PositiveInfinity,-1})
            Assert.False(FootProxyPose.TryCreateCapsules(model,source,new object(),1,time,Capsules(),out _));
    }

    private sealed class EqualToEverything
    {public override bool Equals(object? obj)=>true;public override int GetHashCode()=>0;}
}
