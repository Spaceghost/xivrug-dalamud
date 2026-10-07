using System.Numerics;
using XivCloth.Core;

public sealed class PairedRuntimeTests
{
    private static (XpbdCloth Solver,PairedPlantPolicy Policy,PairedPlantObservation First) Start(Fixture f,XpbdSettings? settings=null,FootProxyPose? reference=null)
    {
        var actual=reference??f.Pose(1,1);
        var solver=new XpbdCloth(f.Definition,settings??new(){Gravity=default,Iterations=2});solver.Reset(reference is null?f.Start:f.InitialFor(actual));
        Assert.True(PairedPlantPolicy.TryCreate(f.Definition,f.Scene,actual,[0,1],[0,1],out var policy));
        Assert.True(policy!.TryObserve(actual,1,out var first));
        Assert.Equal(XpbdStatus.Ready,solver.AdmitPairedScene(first!,1).Status);
        return(solver,policy,first!);
    }
    private static PairedPlantObservation Observe(PairedPlantPolicy policy,FootProxyPose actual,double? now=null)
    {Assert.True(policy.TryObserve(actual,now??actual.SampledAt,out var result));return result!;}
    private static PairedPlantInterval Interval(PairedPlantObservation first,PairedPlantObservation last,double? now=null)
    {Assert.True(PairedPlantInterval.TryCreate(first,last,now??last.Actual.SampledAt,out var result));return result!;}

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FalseOrThrowingCommitRollsBackHistoryCompressionAndVelocity(bool throws)
    {
        var f=new Fixture();var (solver,policy,a)=Start(f);
        var control=new XpbdCloth(f.Definition,new(){Gravity=default,Iterations=2});control.Reset(f.Start);
        Assert.Equal(XpbdStatus.Ready,control.AdmitPairedScene(a,1).Status);
        var b=Observe(policy,f.Pose(2,1.016,.012f,.004f,.05f));var interval=Interval(a,b);
        var before=solver.Capture().Positions.ToArray();
        Assert.Equal(XpbdStatus.RejectedInput,solver.AdvanceMeasuredFeetWithPlants(interval,1.016,
            canCommit:()=>throws?throw new InvalidOperationException():false).Status);
        Assert.Equal(before,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForPlantedFeet(a,a,1.016).Status);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeetWithPlants(interval,1.016).Status);
        Assert.Equal(XpbdStatus.Ready,control.AdvanceMeasuredFeetWithPlants(interval,1.016).Status);
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
        var c=Observe(policy,f.Pose(3,1.032,.02f,.007f,.1f));var next=Interval(b,c);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeetWithPlants(next,1.032).Status);
        Assert.Equal(XpbdStatus.Ready,control.AdvanceMeasuredFeetWithPlants(next,1.032).Status);
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void LateCaptureCannotPrecedeAcceptedCheckOrConsumeTheNextObservation()
    {
        var f=new Fixture();var (solver,policy,a)=Start(f);
        var b=Observe(policy,f.Pose(2,1.016));Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeetWithPlants(Interval(a,b),1.025).Status);
        var before=solver.Capture().Positions.ToArray();var earlier=Observe(policy,f.Pose(3,1.02),1.03);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForPlantedFeet(b,earlier,1.03).Status);
        var later=Observe(policy,f.Pose(3,1.026),1.03);
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForPlantedFeet(b,later,1.03).Status);
        Assert.Equal(before,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeetWithPlants(Interval(b,later,1.03),1.03).Status);
    }

    [Fact]
    public void PairedCaptureAccountsForCoverageAndTreeWorkWithoutChangingOriginalConstructor()
    {
        var f=new Fixture();var (solver,_,a)=Start(f);var result=solver.CaptureForPlantedFeet(a,a,1);
        Assert.Equal(XpbdStatus.Ready,result.Status);Assert.True(result.BroadphaseNodes>0);
        Assert.Equal(result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes,result.WorkUsed);
        Assert.NotNull(typeof(XpbdFootCapture).GetConstructor([typeof(XpbdStatus),typeof(XpbdFrame),typeof(int),typeof(int)]));
        var exhausted=solver.CaptureForPlantedFeet(a,a,1,maximumWork:100);
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,exhausted.Status);Assert.Null(exhausted.Frame);
        Assert.Equal(100,exhausted.WorkUsed);
    }

    [Fact]
    public void RemovalIsWholePoseAdmissionAndOrdinaryContinuationKeepsAcceptedHistory()
    {
        var f=new Fixture();var (solver,policy,a)=Start(f);
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.RemovePlantPolicy(1).Status);
        var previous=a;
        for(var i=1;i<=8;i++)
        {
            var next=Observe(policy,f.Pose(i+1,1+i*.008,.005f*i));
            Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeetWithPlants(Interval(previous,next),next.Actual.SampledAt).Status);
            previous=next;
        }
        Assert.Equal(XpbdStatus.RejectedInput,solver.RemovePlantPolicy(previous.Actual.SampledAt,()=>false).Status);
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForPlantedFeet(previous,previous,previous.Actual.SampledAt).Status);
        var pose=solver.Capture().Positions.ToArray();
        Assert.Equal(XpbdStatus.Ready,solver.RemovePlantPolicy(previous.Actual.SampledAt).Status);
        Assert.Equal(pose,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForFeet(previous.Actual,previous.Actual,previous.Actual.SampledAt).Status);
        var actual=f.Pose(10,1.072,.045f);
        Assert.True(MeasuredFootInterval.TryCreate(previous.Actual,actual,actual.SampledAt,out var interval));
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeet(f.Scene,interval,actual.SampledAt).Status);
    }

    [Fact]
    public void ReplacementAndSameGenerationForeignSceneNeedExactNewAdmission()
    {
        var f=new Fixture();var (solver,policy,a)=Start(f);
        var otherScene=new MeasuredTriangleScene(f.Scene.Generation,f.Scene.Triangles,false,f.Scene.Coverage);
        Assert.True(PairedPlantPolicy.TryCreate(f.Definition,otherScene,a.Actual,[0,1],[0,1],out var other));
        var alternate=Observe(other!,a.Actual);
        var packet=solver.CaptureForPlantedFeet(a,a,1).Frame!;
        Assert.False(packet.FootBinding!.CanPresentPaired(a,a,otherScene,1));
        Assert.Equal(XpbdStatus.RejectedInput,solver.AdmitPairedScene(alternate,1,()=>false).Status);
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForPlantedFeet(a,a,1).Status);
        Assert.Equal(XpbdStatus.Ready,solver.AdmitPairedScene(alternate,1).Status);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForPlantedFeet(a,a,1).Status);
        Assert.Equal(XpbdStatus.Ready,solver.CaptureForPlantedFeet(alternate,alternate,1).Status);
    }

    [Fact]
    public void CoverageRefusalAndExtraWallRetainTheCompleteMaterial()
    {
        var f=new Fixture();var solver=new XpbdCloth(f.Definition,new(){Gravity=default,Iterations=2});solver.Reset(f.Start);
        var foot=f.Pose(1,1);
        foreach(var scene in new[]{new MeasuredTriangleScene(2,f.Scene.Triangles,false,new(new(-1,-1,-1),new(1,2,1))),
            new MeasuredTriangleScene(3,[..f.Scene.Triangles.ToArray(),new(new(0,-1,-2),new(0,2,-2),new(0,-1,2))],true,f.Scene.Coverage)})
        {
            Assert.True(PairedPlantPolicy.TryCreate(f.Definition,scene,foot,[0,1],[0,1],out var policy));
            var observation=Observe(policy!,foot);
            Assert.Equal(XpbdStatus.CollisionUnproven,solver.AdmitPairedScene(observation,1).Status);
            Assert.Equal(f.Start,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
        }
    }

    [Fact]
    public void NegativeGapClonesAndMissingPolicyCannotAcquireLocalExceptions()
    {
        var f=new Fixture();var (solver,policy,a)=Start(f);
        Assert.False(policy.TryObserve(f.Pose(2,1.016,-.000001f),1.016,out _));
        Assert.False(policy.TryObserve(f.Pose(1,1),1,out _));
        var cloned=Observe(policy,a.Actual);
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.CaptureForPlantedFeet(cloned,cloned,1).Status);
        var next=Observe(policy,f.Pose(2,1.016));
        Assert.Equal(XpbdStatus.FootTrackingUnavailable,solver.AdvanceMeasuredFeetWithPlants(Interval(cloned,next),1.016).Status);
        Assert.Equal(XpbdStatus.RejectedInput,solver.CaptureForFeet(a.Actual,a.Actual,1).Status);
        Assert.Equal(XpbdStatus.RejectedInput,solver.RebindScene(f.Scene).Status);
        Assert.Equal(XpbdStatus.RejectedInput,solver.Advance(.008,f.Scene).Status);
        Assert.Equal(XpbdStatus.Ready,solver.AdvanceMeasuredFeetWithPlants(Interval(a,next),1.016).Status);
    }

    [Theory]
    [InlineData(0)] [InlineData(.32f)]
    public void BothFootOverlapAndSmallObservedXzTravelReturnWithoutLosingFaces(float inward)
    {
        var f=new Fixture();var (solver,policy,previous)=Start(f,reference:f.Pose(1,1,inward:inward));
        var initial=solver.Capture();
        for(var i=1;i<=12;i++)
        {
            var shift=.012f*MathF.Sin(i*MathF.PI/6);
            var next=Observe(policy,f.Pose(i+1,1+i*.008,xShift:shift,inward:inward));
            var result=solver.AdvanceMeasuredFeetWithPlants(Interval(previous,next),next.Actual.SampledAt);
            Assert.True(result.Status==XpbdStatus.Ready,$"inward={inward} step={i}: {result}");
            Assert.Equal(initial.Indices.ToArray(),solver.Capture().Indices.ToArray());
            Assert.Equal(XpbdStatus.Ready,solver.CaptureForPlantedFeet(next,next,next.Actual.SampledAt).Status);
            previous=next;
        }
        Assert.True(solver.Capture().Positions.ToArray().Max(p=>p.Y)>.015f);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void OrderedUnequalLiftRotationRecoveryAndReplantKeepFreeFolds(bool circle)
    {
        var f=new Fixture(circle);var solver=new XpbdCloth(f.Definition,new(){Gravity=default,Iterations=2});
        solver.Reset(f.Start);var first=f.Pose(1,1);
        Assert.True(PairedPlantPolicy.TryCreate(f.Definition,f.Scene,first,[0,1],[0,1],out var policy));
        Assert.True(policy!.TryObserve(first,1,out var previous));
        Assert.Equal(XpbdStatus.Ready,solver.AdmitPairedScene(previous!,1).Status);
        var initial=solver.Capture();var recovered=false;var changedOutside=false;
        for(var frame=1;frame<=45;frame++)
        {
            var lift=frame<=8?0:frame<=18?(frame-8)*.005f:frame<=28?.05f:frame<=38?(38-frame)*.005f:0;
            var actual=f.Pose(frame+1,1+frame*.008,lift,lift*.4f,lift*6);
            Assert.True(policy.TryObserve(actual,actual.SampledAt,out var observed));
            Assert.True(PairedPlantInterval.TryCreate(previous!,observed!,actual.SampledAt,out var interval));
            var result=solver.AdvanceMeasuredFeetWithPlants(interval!,actual.SampledAt);
            Assert.True(result.Status==XpbdStatus.Ready,$"frame={frame} lift={lift:R} result={result}");
            var capture=solver.CaptureForPlantedFeet(observed!,observed!,actual.SampledAt);
            Assert.Equal(XpbdStatus.Ready,capture.Status);var pose=capture.Frame!;
            Assert.Equal(initial.Indices.ToArray(),pose.Indices.ToArray());Assert.Equal(initial.UV.ToArray(),pose.UV.ToArray());
            Assert.Equal(81,pose.Positions.Length);
            Assert.True(pose.FootBinding!.CanPresentPaired(observed,observed,f.Scene,actual.SampledAt));
            Assert.False(pose.FootBinding.CanPresent(actual,actual,f.Scene.Generation,actual.SampledAt));
            if(frame==25)recovered=pose.Positions[40].Y>=.004f;
            for(var i=0;i<pose.Positions.Length;i++)if(f.Pattern.Positions[i].Length()>1.2f
                &&Vector3.Distance(pose.Positions[i],initial.Positions[i])>1e-5)changedOutside=true;
            Assert.True(pose.Positions.ToArray().Max(p=>p.Y)>.015f,"The whole material must not be flattened by the plant.");
            previous=observed;
        }
        Assert.True(recovered,"The solver, not manually edited endpoints, must recover positive thickness after lift.");
        Assert.True(changedOutside,"Free outer material must genuinely deform.");
        Assert.Equal(0,solver.Capture().Positions[40].Y);
    }

    [Fact]
    public void SharedPatternWithFreeFoldsCanAdvanceAtAPlant()
    {
        var f=new Fixture();var a=f.Pose(1,1);var b=f.Pose(2,1.016);
        Assert.True(MeasuredFootInterval.TryCreate(a,b,b.SampledAt,out var interval));
        var solver=new XpbdCloth(f.Definition,new(){Gravity=default,Iterations=2});
        solver.Reset(f.Start);
        Assert.True(PairedPlantPolicy.TryCreate(f.Definition,f.Scene,a,[0,1],[0,1],out var policy));
        Assert.True(policy!.TryObserve(a,a.SampledAt,out var first));
        Assert.True(policy.TryObserve(b,b.SampledAt,out var last));
        Assert.Equal(XpbdStatus.Ready,solver.AdmitPairedScene(first!,a.SampledAt).Status);
        Assert.True(PairedPlantInterval.TryCreate(first!,last!,b.SampledAt,out var paired));
        var result=solver.AdvanceMeasuredFeetWithPlants(paired!,b.SampledAt);
        Assert.Equal(XpbdStatus.Ready,result.Status);
    }

    internal sealed class Fixture
    {
        public readonly ClothRestPattern Pattern;
        public readonly XpbdDefinition Definition;
        public readonly Vector3[] Start;
        public readonly object Source=new();
        public readonly FootProxyModelBinding Model;
        public readonly MeasuredTriangleScene Scene=new(1,
            [new(new(-4,0,-4),new(-4,0,4),new(4,0,-4)),
             new(new(4,0,-4),new(-4,0,4),new(4,0,4))],false,
            new(new(-5,-1,-5),new(5,3,5)));
        public Fixture(bool circle=true)
        {
            Pattern=circle?ClothRestPattern.Circle(3,9):ClothRestPattern.RoundedRectangle(3,2.5f,.4f,9,9);
            Definition=Pattern.ToDefinition();Start=Pattern.Positions.ToArray();
            for(var i=0;i<Start.Length;i++)
            {
                var p=Start[i];
                Start[i].Y=.03f+.03f*MathF.Sin(p.X*4+p.Z*3)*MathF.Sin(p.X*4+p.Z*3);
            }
            var ids=Pattern.Indices;
            for(var i=0;i<ids.Length;i+=3)
            {
                var a=Start[ids[i]];var b=Start[ids[i+1]];var c=Start[ids[i+2]];
                var lo=Vector3.Min(a,Vector3.Min(b,c));var hi=Vector3.Max(a,Vector3.Max(b,c));
                foreach(var x in new[]{-.35f,.35f})
                    if(lo.X<=x+.12901f&&hi.X>=x-.12901f&&lo.Z<=.20901f&&hi.Z>=-.20901f)
                        Start[ids[i]].Y=Start[ids[i+1]].Y=Start[ids[i+2]].Y=0;
            }
            Assert.True(FootProxyModelBinding.TryCreate(new(3,12,34,1),Source,out var model));Model=model!;
        }
        public FootProxyPose Pose(long sequence,double time,float lift=0,float toeLift=0,float yaw=0,float xShift=0,float inward=0)
        {
            Span<FootCapsule> feet=stackalloc FootCapsule[2];
            var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
            for(var i=0;i<2;i++)
            {
                var center=new Vector3((i==0?-.35f+inward:.35f-inward)+xShift,.125f+lift,0);
                feet[i]=new(center+Vector3.Transform(new Vector3(0,0,-.08f),rotation),
                    center+Vector3.Transform(new Vector3(0,toeLift,.08f),rotation),.125f);
            }
            Assert.True(FootProxyPose.TryCreateCapsules(Model,Source,new object(),sequence,time,feet,out var pose));return pose!;
        }
        public Vector3[] InitialFor(FootProxyPose reference)
        {
            var result=Pattern.Positions.ToArray();
            for(var i=0;i<result.Length;i++)result[i].Y=.03f+.03f*MathF.Sin(result[i].X*4+result[i].Z*3)*MathF.Sin(result[i].X*4+result[i].Z*3);
            var ids=Pattern.Indices;
            for(var i=0;i<ids.Length;i+=3)
            {
                var a=result[ids[i]];var b=result[ids[i+1]];var c=result[ids[i+2]];
                var lo=Vector3.Min(a,Vector3.Min(b,c));var hi=Vector3.Max(a,Vector3.Max(b,c));
                foreach(var foot in reference.Capsules)
                {
                    var minimum=Vector3.Min(foot.A,foot.B)-new Vector3(foot.Radius+.00401f);
                    var maximum=Vector3.Max(foot.A,foot.B)+new Vector3(foot.Radius+.00401f);
                    if(lo.X<=maximum.X&&hi.X>=minimum.X&&lo.Z<=maximum.Z&&hi.Z>=minimum.Z)
                        result[ids[i]].Y=result[ids[i+1]].Y=result[ids[i+2]].Y=0;
                }
            }
            return result;
        }
    }
}
