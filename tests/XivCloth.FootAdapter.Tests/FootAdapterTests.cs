using System.Numerics;
using XivCloth.Core;
using XivRug.Prototype;
using XivSurface.Core;

public sealed class FootAdapterTests
{
    private static readonly ClothFootCaptureIdentity Owner=new(128,42,(nint)7,1);
    private static ClothFootContact[] Contacts(float y=.03f)=>[new(new(-.1f,0),y,.22f),new(new(.1f,0),y,.22f),new(new(-.1f,.5f),y,.22f),new(new(.1f,.5f),y,.22f)];
    [Fact]
    public void ActualPublishedFrameConvertsWithoutRetimestampingOrInventingNewOffsets()
    {
        var publisher=new ClothFootRenderPublisher();publisher.BeginUpdate(Owner);
        var actual=publisher.PublishEarlyCapture(Owner,Owner,10,10.001,Contacts());
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,actual,10.002,out var proxy));
        Assert.NotNull(proxy);Assert.Equal(10,proxy.SampledAt);Assert.Equal(Owner.PlayerId,proxy.Identity.Actor);
        Assert.Equal(2,proxy.Capsules.Length);
        var foot=proxy.Capsules[0];Assert.Equal(.22f,foot.Radius);Assert.Equal(-.1f,foot.A.X);Assert.Equal(.1f,foot.B.X);
        Assert.True((double)foot.A.Y-foot.Radius<=.03f);Assert.InRange(.03f-((double)foot.A.Y-foot.Radius),0,.000001);
    }
    [Fact]
    public void ActualFootBoneConversionStillRepresentsAnEstimateNotEquipmentVertices()
    {
        Assert.True(ClothFootClearance.TryBoot(new(-.1f,.1f,0),new(.1f,.08f,0),default,Vector3.One,out var heel,out var toe));
        ClothFootContact[] contacts=[heel,toe,heel with{Center=heel.Center+new Vector2(0,.5f)},toe with{Center=toe.Center+new Vector2(0,.5f)}];
        var actual=new ClothFootRenderFrame(128,10,contacts,captureIdentity:Owner);
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,actual,10,out var proxy));
        Assert.NotNull(proxy);Assert.InRange(heel.FootY-((double)proxy.Capsules[0].A.Y-proxy.Capsules[0].Radius),0,.000001);
        // SoleY is the current TryBoot estimate, not replaced by a floor ray.
    }
    [Fact]
    public void MissingPartialFallbackSuppressedAndExpiredActualFramesRefuse()
    {
        var frames=new ClothFootRenderFrame?[]{null,new(128,10,Contacts()[..2]),
            new(128,10,ClothFootClearance.WithFallback([],Vector3.Zero)),new(128,10,Contacts(),true),new(128,9,Contacts())};
        foreach(var frame in frames)Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,frame,10,out _));
    }
    [Fact]
    public void WrongOwnerZoneModelAndClockDoNotRejuvenateAFrame()
    {
        var frame=new ClothFootRenderFrame(128,10,Contacts(),captureIdentity:Owner);
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner with{PlayerId=99},1,1,frame,10,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,0,1,frame,10,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner with{Zone=129},Owner with{Zone=129},1,1,frame,10,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,frame,9.999,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,frame,double.NaN,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,frame,10.05,out _));
    }
    [Fact]
    public void SuccessiveRealCaptureShapesProduceBoundedProxyMotion()
    {
        var owner=Owner with{ModelGeneration=3};
        var a=new ClothFootRenderFrame(128,10,Contacts(.03f),captureIdentity:owner);var b=new ClothFootRenderFrame(128,10+1d/60,Contacts(.04f),captureIdentity:owner);
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(owner,owner,3,1,a,10,out var before));
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(owner,owner,3,2,b,b.SampledAt,out var after));
        Assert.True(FootProxyMotion.TryCreate(before,after,b.SampledAt,out var motion));
        Assert.NotNull(motion);Assert.Equal(a.SampledAt,motion.Before.SampledAt);Assert.Equal(b.SampledAt,motion.After.SampledAt);
    }
    [Fact]
    public void AdaptiveRendererAgeNeverWidensTheCapsuleModelsOwnMotionHorizon()
    {
        var adaptive=new ClothFootRenderFrame(128,10,Contacts(),clothMaximumAgeSeconds:.1,captureIdentity:Owner);
        Span<ClothFootContact> contacts=stackalloc ClothFootContact[4];
        Assert.True(adaptive.TryGetClothContacts(128,10.05,contacts,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,adaptive,10.05,out _));
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,adaptive,10+1d/30,out _));
    }

    [Fact]
    public void UnboundLegacyFrameCannotBeRelabeledByLaterOwnerArguments()
    {
        var legacy=new ClothFootRenderFrame(128,10,Contacts());
        Assert.True(legacy.CanRender(128,10));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,legacy,10,out _));
        var legacyOwner=Owner with{ModelGeneration=0};
        var publisher=new ClothFootRenderPublisher();publisher.BeginUpdate(legacyOwner);
        var frame=publisher.CompleteUpdate(true,legacyOwner,legacyOwner,10,10.001,Contacts());
        Assert.NotNull(frame);Assert.False(frame.CaptureIdentity.Bound);
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,frame,10.002,out _));
    }

    [Fact]
    public void StoredOwnerAndModelGenerationAreAuthoritativeEvenInTheSameZone()
    {
        Assert.True(ClothFootRenderFrame.TryCaptureBound(Owner,Owner,10,10.001,Contacts(),out var frame));
        foreach(var changed in new[]{Owner with{PlayerId=43},Owner with{Address=(nint)8},Owner with{ModelGeneration=2}})
            Assert.False(FootMarkerSnapshotAdapter.TryCapture(changed,changed,changed.ModelGeneration,1,frame,10.002,out _));
        Assert.False(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,2,1,frame,10.002,out _));
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,frame,10.002,out var pose));
        Assert.NotNull(pose);Assert.Equal(10,pose.SampledAt);Assert.Equal(1,pose.Identity.ModelGeneration);
    }

    [Fact]
    public void ModelReplacementCannotContinueAFormerCapsuleMotionEvenAtTheSameAddress()
    {
        var nextOwner=Owner with{ModelGeneration=2};
        Assert.True(ClothFootRenderFrame.TryCaptureBound(Owner,Owner,10,10.001,Contacts(),out var old));
        Assert.True(ClothFootRenderFrame.TryCaptureBound(nextOwner,nextOwner,10.01,10.011,Contacts(),out var current));
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(Owner,Owner,1,1,old,10.001,out var before));
        Assert.True(FootMarkerSnapshotAdapter.TryCapture(nextOwner,nextOwner,2,2,current,10.011,out var after));
        Assert.False(FootProxyMotion.TryCreate(before,after,10.011,out _));
    }
}
