using System.Numerics;
using System.Text.Json;
using XivCloth.Core;
using XivRug.Rendering;
using XivRug.Prototype;
using XivSurface.Core;

namespace XivCloth.Owner.Tests;

public sealed class PairedOwnerTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void RealRawCalibrationAndRotationPublishFullSharedPlantRecoveryReplant(bool circle)
    {
        var f=new Fixture(circle);var initial=f.Owner.Inspect();var recovered=false;var moved=false;
        for(var step=1;step<=16;step++)
        {
            var lift=step<=2?0:step<=7?(step-2)*.01f:step<=10?.05f:step<=15?(15-step)*.01f:0;
            var actual=f.Next(lift,lift*.4f,lift*2,lift*5);
            var result=f.Owner.ObservePaired(f.Lease,actual);
            Assert.True(result.Status==PhysicalOwnerStatus.Ready,$"step={step} result={result}");
            Assert.True(f.Owner.TryGet(3,out var packet));Assert.True(f.Owner.CanSubmit(packet,3));
            var frame=f.Owner.Inspect();
            Assert.Equal(frame.Positions.ToArray(),packet!.Pose.Positions.ToArray());
            Assert.Equal(initial.Indices.ToArray(),frame.Indices.ToArray());Assert.Equal(initial.UV.ToArray(),frame.UV.ToArray());
            Assert.Equal(81,frame.Positions.Length);Assert.Equal(384,frame.Indices.Length);
            Assert.True(frame.Positions.ToArray().Max(p=>p.Y)>.015f);
            if(step==10)recovered=frame.Positions[40].Y>=.004f;
            for(var i=0;i<frame.Positions.Length;i++)if(f.Pattern.Positions[i].Length()>1.2f
                &&Vector3.Distance(frame.Positions[i],initial.Positions[i])>1e-5)moved=true;
            if(step is 2 or 10 or 16)Export(f,result.Step,frame,actual,circle,step==2?"plant":step==10?"recovery":"replant");
        }
        Assert.True(recovered);Assert.True(moved);Assert.Equal(0,f.Owner.Inspect().Positions[40].Y);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PairedPacketCannotSubmitAfterSourceRevocationOrExpiry(bool expire)
    {
        var f=new Fixture(lifetime:expire?.01:.25);Assert.Equal(PhysicalOwnerStatus.Ready,f.Owner.ObservePaired(f.Lease,f.Next()).Status);
        Assert.True(f.Owner.TryGet(3,out var packet));
        if(expire){f.Now=f.Lease.ExpiresAt+.00001;Assert.True(f.Last.Actual.FreshAt(f.Now));}else f.World.Invalidate();
        Assert.False(f.Owner.CanSubmit(packet,3));Assert.False(f.Owner.TryGet(3,out _));
    }

    [Fact]
    public void SourceRevocationDuringPairedCommitCannotAdvanceAcceptedHistory()
    {
        var f=new Fixture();var before=f.Owner.Inspect().Positions.ToArray();var accepted=f.Owner.AcceptedObservation;
        var actual=f.Next(.01f);var reads=0;f.OnClock=()=>{if(++reads==2)f.World.Invalidate();};
        Assert.Equal(PhysicalOwnerStatus.SimulationRefused,f.Owner.ObservePaired(f.Lease,actual).Status);
        f.OnClock=null;Assert.Equal(before,f.Owner.Inspect().Positions.ToArray());Assert.Same(accepted,f.Owner.AcceptedObservation);
        Assert.False(f.Owner.TryGet(3,out _));
    }

    [Fact]
    public void EqualForeignPacketAndWrongLeasePolicyCannotBePresented()
    {
        var f=new Fixture();var g=new Fixture();
        Assert.Equal(PhysicalOwnerStatus.Ready,f.Owner.ObservePaired(f.Lease,f.Next()).Status);
        Assert.Equal(PhysicalOwnerStatus.Ready,g.Owner.ObservePaired(g.Lease,g.Next()).Status);
        Assert.True(f.Owner.TryGet(3,out var packet));Assert.False(g.Owner.CanSubmit(packet,3));
        var second=f.World.Capture(3,new(new(-5,-1,-5),new(5,3,5)),f.Now);
        Assert.Equal(XpbdStatus.RejectedInput,f.Owner.AdmitPairedScene(second,f.Last).Status);
    }

    [Fact]
    public void ActualCalibrationReplacementAndRelabelledRawReferenceRefuse()
    {
        var f=new Fixture();var actual=f.Next(.01f,.002f,.02f);
        Assert.Equal(PhysicalOwnerStatus.Ready,f.Owner.ObservePaired(f.Lease,actual).Status);
        var raw=Fixture.Raw(10,f.Now+.008,.01f);var support=Fixture.Support(raw,f.Lease.Scene);
        Assert.True(f.Calibration.TryEvaluate(raw,support,raw.ReadCompletedAt,out var evaluated));
        var clone=Fixture.Raw(10,raw.SampledAt,.01f);
        Assert.False(f.Adapter.TryCapture(f.Policy,evaluated!,clone,raw.ReadCompletedAt,out _));
        var lastCalibration=Fixture.Raw(3,2.04);
        Assert.True(FootPlantCalibration.TryCreate([Fixture.Support(Fixture.Raw(1,2),f.Lease.Scene),
            Fixture.Support(Fixture.Raw(2,2.02),f.Lease.Scene),Fixture.Support(lastCalibration,f.Lease.Scene)],lastCalibration.ReadCompletedAt,out var replacement));
        var next=Fixture.Raw(4,2.048);
        Assert.True(replacement!.TryEvaluate(next,Fixture.Support(next,f.Lease.Scene),next.ReadCompletedAt,out var changed));
        Assert.False(f.Adapter.TryCapture(f.Policy,changed!,next,next.ReadCompletedAt,out _));
    }

    private static void Export(Fixture f,XpbdAdvance result,XpbdFrame frame,PairedPlantObservation foot,bool circle,string label)
    {
        var destination=Environment.GetEnvironmentVariable("XIV_PAIRED_EXPORT_ROOT");if(string.IsNullOrEmpty(destination))return;
        var manifest=Environment.GetEnvironmentVariable("XIV_PAIRED_SOURCE_MANIFEST");
        Assert.True(manifest is {Length:71}&&manifest.StartsWith("sha256:"));
        float[] V(Vector3 v)=>[v.X,v.Y,v.Z];
        var parameters=Enumerable.Range(0,2).Select(i=>
        {
            var actual=foot.Actual.Capsules[i];var reference=f.Policy.Reference.Capsules[i];
            // Inspector geometry is a float representation of the DECLARED
            // model; exact observed capsules and defining displacement remain.
            var a=actual.A with{Y=(float)((double)reference.Radius+actual.A.Y-reference.A.Y)};
            var b=actual.B with{Y=(float)((double)reference.Radius+actual.B.Y-reference.B.Y)};
            return new{a=V(a),b=V(b),radius=actual.Radius,model="plane-parametric",observedA=V(actual.A),observedB=V(actual.B),
                referenceA=V(reference.A),referenceB=V(reference.B),treadY=0,sequence=foot.Actual.Sequence,sampledAt=foot.Actual.SampledAt};
        }).ToArray();
        var snapshot=new{label=$"SYNTHETIC FIXTURE paired {(circle?"circle":"rounded")} {label}; not game capture",status=result.Status.ToString(),
            sourceManifest=manifest,sceneGeneration=frame.SceneGeneration,
            positions=frame.Positions.ToArray().Select(V).ToArray(),uv=frame.UV.ToArray().Select(v=>new[]{v.X,v.Y}).ToArray(),indices=frame.Indices.ToArray(),
            ground=f.Lease.Scene.Triangles.ToArray().Select(t=>new[]{V(t.A),V(t.B),V(t.C)}).ToArray(),feet=parameters,
            compressedVertices=Array.Empty<int>(),transitionFaces=Array.Empty<int>(),step=result};
        Directory.CreateDirectory(destination);
        using var stream=new FileStream(Path.Combine(destination,$"{(circle?"circle":"rounded")}-{label}.json"),FileMode.CreateNew);
        JsonSerializer.Serialize(stream,snapshot,new JsonSerializerOptions{WriteIndented=true});
    }

    private sealed class Fixture
    {
        public readonly FixtureCollisionWorld World;
        public readonly FixtureSceneLease Lease;
        public readonly PhysicalClothRuntime Owner;
        public readonly ClothRestPattern Pattern;
        public readonly FootPlantCalibration Calibration;
        public readonly PairedPlantPolicy Policy;
        public readonly CalibratedPlantSnapshotAdapter Adapter;
        public PairedPlantObservation Last;
        public double Now=1.0401;public Action? OnClock;
        private long sequence=3;
        private static readonly MeasuredTriangle[] Floor=[new(new(-4,0,-4),new(-4,0,4),new(4,0,-4)),new(new(4,0,-4),new(-4,0,4),new(4,0,4))];
        public Fixture(bool circle=true,double lifetime=.25)
        {
            World=new(Floor);Lease=World.Capture(3,new(new(-5,-1,-5),new(5,3,5)),Now,lifetime);
            Pattern=circle?ClothRestPattern.Circle(3,9):ClothRestPattern.RoundedRectangle(3,2.5f,.4f,9,9);
            var initial=Pattern.Positions.ToArray();for(var i=0;i<initial.Length;i++)initial[i].Y=.03f+.03f*MathF.Sin(initial[i].X*4+initial[i].Z*3)*MathF.Sin(initial[i].X*4+initial[i].Z*3);
            var ids=Pattern.Indices;
            for(var i=0;i<ids.Length;i+=3)
            {
                var a=initial[ids[i]];var b=initial[ids[i+1]];var c=initial[ids[i+2]];
                var lo=Vector3.Min(a,Vector3.Min(b,c));var hi=Vector3.Max(a,Vector3.Max(b,c));
                foreach(var x in new[]{-.3f,.3f})if(lo.X<=x+.22401f&&hi.X>=x-.22401f&&lo.Z<=.32401f&&hi.Z>=-.32401f)
                    initial[ids[i]].Y=initial[ids[i+1]].Y=initial[ids[i+2]].Y=0;
            }
            Owner=new(Pattern,Vector3.Zero,new(Vector2.Zero,new(1.5f,circle?1.5f:1.25f),circle,circle?0:.4f),
                ()=>{OnClock?.Invoke();return Now;},new(){Gravity=default,Iterations=2},initial);
            var reference=Raw(3,1.04);
            Assert.True(FootPlantCalibration.TryCreate([Support(Raw(1,1),Lease.Scene),Support(Raw(2,1.02),Lease.Scene),Support(reference,Lease.Scene)],Now,out var calibration));
            Calibration=calibration!;
            Assert.True(Calibration.TryEvaluate(reference,Support(reference,Lease.Scene),Now,out var evaluated));
            var ordinary=new CalibratedFootSnapshotAdapter();Assert.True(ordinary.TryCapture(evaluated,reference,Now,out var actual));
            Assert.True(Owner.TryCreatePlantPolicy(Lease,actual!,[0,1],[0,1],out var policy));Policy=policy!;
            Assert.True(CalibratedPlantSnapshotAdapter.TryBind(ordinary,evaluated!,Policy,Now,out var adapter));Adapter=adapter!;
            Assert.True(Adapter.TryCapture(Policy,evaluated!,reference,Now,out var first));Last=first!;
            Assert.Equal(XpbdStatus.Ready,Owner.AdmitPairedScene(Lease,Last).Status);
        }
        public PairedPlantObservation Next(float lift=0,float toe=0,float pitch=0,float yaw=0)
        {
            sequence++;var sample=1.04+(sequence-3)*.008;Now=sample+.0001;
            var raw=Raw(sequence,sample,lift,toe,pitch,yaw);
            Assert.True(Calibration.TryEvaluate(raw,Support(raw,Lease.Scene),Now,out var evaluated));
            Assert.True(Adapter.TryCapture(Policy,evaluated!,raw,Now,out var result));
            Assert.Same(raw,result!.Actual.CaptureBinding);
            for(var i=0;i<2;i++)
            {Assert.Equal(evaluated!.Capsules[i].A,result.Actual.Capsules[i].A);Assert.Equal(evaluated.Capsules[i].B,result.Actual.Capsules[i].B);}
            Last=result;return result;
        }
        public static RawFootFrame Raw(long sequence,double time,float lift=0,float toe=0,float pitch=0,float yaw=0)
        {
            RawFootIdentity identity=new(new(3,12,34,2),56,78,Vector3.One,1);
            var rotation=Quaternion.CreateFromYawPitchRoll(yaw,pitch,0);
            RawFootMarker[] markers=[new(new(-.3f,.04f+lift,-.1f),rotation),new(new(-.3f,.06f+lift+toe,.1f),rotation),
                new(new(.3f,.04f+lift,-.1f),rotation),new(new(.3f,.06f+lift+toe,.1f),rotation)];
            Assert.True(RawFootFrame.TryCapture(identity,identity,sequence,time,time+.0001,Vector3.Zero,markers,out var result));return result!;
        }
        public static FootSupportObservation Support(RawFootFrame raw,MeasuredTriangleScene scene)
        {
            var faces=new FloorTriangle[4];
            for(var i=0;i<4;i++)
            {var p=raw.Markers[i].Position;var t=scene.Triangles[p.X+p.Z<=0?0:1];faces[i]=new(t.A,t.B,t.C);}
            Assert.True(FootSupportObservation.TryCreate(raw,scene.Generation,faces,out var result));return result!;
        }
    }
}
