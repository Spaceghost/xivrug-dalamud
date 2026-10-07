using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootCaptureBindingTests
{
    private static readonly ClothFootCaptureIdentity Actor=new(128,42,(nint)7);
    private static ClothFootModelObservation Model=>new(Actor,(nint)11,(nint)12,(nint)13,100);
    private static ClothFootContact[] Contacts()=>[new(new(-.1f,0),.03f,.22f),new(new(.1f,0),.03f,.22f),
        new(new(-.1f,.5f),.03f,.22f),new(new(.1f,.5f),.03f,.22f)];

    [Fact]
    public void StableObservedNativeIdentityKeepsItsGenerationWithoutTrustingCallerGeneration()
    {
        var tracker=new ClothFootModelTracker();var first=tracker.Observe(Model);
        Assert.True(first.Bound);Assert.Equal(1,first.ModelGeneration);
        Assert.Equal(first,tracker.Observe(Model));
        Assert.Equal(first,tracker.Observe(Model with{Actor=Actor with{ModelGeneration=999}}));
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void EveryActualActorModelIdentityComponentBreaksContinuity(int field)
    {
        var tracker=new ClothFootModelTracker();var first=tracker.Observe(Model);
        var changed=field switch
        {
            0=>Model with{Actor=Actor with{Zone=129}},1=>Model with{Actor=Actor with{PlayerId=43}},
            2=>Model with{Actor=Actor with{Address=(nint)8}},3=>Model with{Draw=(nint)21},
            4=>Model with{Skeleton=(nint)22},5=>Model with{HavokSkeleton=(nint)23},_=>Model with{BoneCount=101},
        };
        var next=tracker.Observe(changed);Assert.True(next.Bound);Assert.Equal(first.ModelGeneration+1,next.ModelGeneration);
        Assert.Equal(next,tracker.Observe(changed));
        Assert.Equal(next.ModelGeneration+1,tracker.Observe(Model).ModelGeneration);
    }

    [Fact]
    public void MissingModelGapAndExplicitInvalidationNeverRecycleAnEarlierGeneration()
    {
        var tracker=new ClothFootModelTracker();var first=tracker.Observe(Model);
        Assert.False(tracker.Observe(Model with{HavokSkeleton=0}).Valid);Assert.False(tracker.Current.Valid);
        var second=tracker.Observe(Model);Assert.Equal(first.ModelGeneration+1,second.ModelGeneration);
        tracker.Invalidate();Assert.Equal(second.ModelGeneration+1,tracker.Observe(Model).ModelGeneration);
        Assert.False(tracker.Observe(Model with{BoneCount=1025}).Valid);
        Assert.False(tracker.Observe(Model with{Actor=Actor with{ModelGeneration=-1}}).Valid);
    }

    [Fact]
    public void BoundTransactionCopiesContactsAndRetainsOriginalOwnerAndReadStart()
    {
        var owner=new ClothFootModelTracker().Observe(Model);var contacts=Contacts();
        Assert.True(ClothFootRenderFrame.TryCaptureBound(owner,owner,10,10.001,contacts,out var frame));
        Assert.NotNull(frame);Assert.Equal(owner,frame.CaptureIdentity);Assert.Equal(10,frame.SampledAt);
        contacts[0]=default;Assert.True(frame.Contacts[0].Valid);
        Assert.True(frame.CanRender(128,10.002));
    }

    [Fact]
    public void ChangedUnboundPartialOrSlowReadCannotPublishBoundFrame()
    {
        var owner=new ClothFootModelTracker().Observe(Model);
        foreach(var after in new[]{owner with{PlayerId=43},owner with{ModelGeneration=2},default})
            Assert.False(ClothFootRenderFrame.TryCaptureBound(owner,after,10,10.001,Contacts(),out _));
        Assert.False(ClothFootRenderFrame.TryCaptureBound(Actor,Actor,10,10.001,Contacts(),out _));
        Assert.False(ClothFootRenderFrame.TryCaptureBound(owner,owner,10,10.05,Contacts(),out _));
        Assert.False(ClothFootRenderFrame.TryCaptureBound(owner,owner,10,9.99,Contacts(),out _));
        Assert.False(ClothFootRenderFrame.TryCaptureBound(owner,owner,10,double.NaN,Contacts(),out _));
        Assert.False(ClothFootRenderFrame.TryCaptureBound(owner,owner,10,10.001,Contacts().AsSpan(0,2),out _));
        Assert.False(ClothFootRenderFrame.TryCaptureBound(owner,owner,10,10.001,[..Contacts(),Contacts()[0]],out _));
    }

    [Fact]
    public void PublisherBindsValidatedTransactionAndInvalidatesOnModelReplacement()
    {
        var owner=new ClothFootModelTracker().Observe(Model);var publisher=new ClothFootRenderPublisher();
        publisher.BeginUpdate(owner);
        var frame=publisher.PublishEarlyCapture(owner,owner,10,10.001,Contacts());
        Assert.NotNull(frame);Assert.Equal(owner,frame.CaptureIdentity);
        Assert.Null(publisher.CompleteUpdate(true,owner,owner with{ModelGeneration=2},10.002,10.003,Contacts()));
        Assert.Null(publisher.Current);
        Assert.Null(publisher.CompleteUpdate(true,owner,owner,10.004,10.005,Contacts()));
    }

    [Fact]
    public void LegacyConstructorAndPublisherRemainRenderCompatibleButNeverBound()
    {
        var direct=new ClothFootRenderFrame(128,10,Contacts());
        Assert.True(direct.CanRender(128,10));Assert.False(direct.CaptureIdentity.Bound);
        var publisher=new ClothFootRenderPublisher();publisher.BeginUpdate(Actor);
        var frame=publisher.CompleteUpdate(true,Actor,Actor,10,10.001,Contacts());
        Assert.NotNull(frame);Assert.True(frame.CanRender(128,10.001));Assert.Equal(Actor,frame.CaptureIdentity);
        Assert.False(frame.CaptureIdentity.Bound);
        Assert.Throws<ArgumentException>(()=>new ClothFootRenderFrame(129,10,Contacts(),captureIdentity:Actor));
    }
}
