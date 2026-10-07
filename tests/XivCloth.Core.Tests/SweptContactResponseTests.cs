using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class SweptContactResponseTests
{
    [Theory]
    [InlineData(.04f,true)][InlineData(.026f,false)]
    public void PublicFootAdvanceCertifiesTheTerrainRepairedPoseNotTheOldProposal(float footOffset,bool expectedReady)
    {
        Vector3[] before=[new(.47777557f,.445273f,-.22968818f),new(.26278365f,.3852652f,.009999165f),new(.50294024f,.45551535f,.013261441f)];
        Vector3[] proposal=[new(.46335495f,.43446007f,-.23022887f),new(.24765293f,.37745076f,.009570875f),new(.48914185f,.4420544f,.012655333f)];
        var scene=new MeasuredTriangleScene(1,[new(new(.5f,.45000002f,0),new(.5f,.3f,.375f),new(.5f,.45000002f,.375f))]);
        var definition=new XpbdDefinition(before,new Vector2[3],[0,1,2],[1,1,1],
            [new(0,1,MaterialEdge.Stretch),new(1,2,MaterialEdge.Stretch),new(2,0,MaterialEdge.Stretch)]);
        var settings=new XpbdSettings{Iterations=1,Bending=false,Gravity=default,Drag=0};
        var solver=new XpbdCloth(definition,settings);
        var inputs=new XpbdStepInputs(definition,targets:[new(0,proposal[0],0),new(1,proposal[1],0),new(2,proposal[2],0)]);
        var normal=Vector3.Normalize(Vector3.Cross(before[1]-before[0],before[2]-before[0]));
        FootProxyMotion Motion(float offset)
        {
            FootProxyPose Pose(Vector3 point,long sequence,double time)
            {
                const float radius=.02f;var at=point+normal*offset;var sole=at.Y-radius;
                FootMarkerEnvelope[] markers=[new(new(at.X,at.Z),sole,radius),new(new(at.X,at.Z),sole,radius),
                    new(new(2,2),sole,radius),new(new(2,2),sole,radius)];
                Assert.True(FootProxyPose.TryCreate(new(1,1,1,1),sequence,time,markers,out var pose));return pose!;
            }
            var first=Pose(before[2],1,10);var last=Pose(proposal[2],2,10+XpbdCloth.FixedStep);
            Assert.True(FootProxyMotion.TryCreate(first,last,last.SampledAt,out var motion));return motion!;
        }
        var feet=Motion(footOffset);var work=new CollisionWork(100_000);
        Assert.Equal(SweepVerdict.Clear,FootCapsuleContacts.Sweep(before,proposal,[0,1,2],
            feet.Before.Capsules,feet.After.Capsules,.002f,ref work));
        var repaired=proposal.ToArray();
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.Sweep(before,repaired,[0,1,2],scene,.002f,ref work,out var hint));
        Assert.True(TriangleContacts.RespondToSweep(before,repaired,[1,1,1],new float[3],hint,.004f,ref work));
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.Sweep(before,repaired,[0,1,2],scene,.002f,ref work));
        Assert.Equal(expectedReady?SweepVerdict.Clear:SweepVerdict.Unproven,FootCapsuleContacts.Sweep(before,repaired,[0,1,2],
            feet.Before.Capsules,feet.After.Capsules,.002f,ref work));
        var result=solver.AdvanceWithFeet(XpbdCloth.FixedStep,scene,feet,feet.After.SampledAt,inputs);
        Assert.Equal(expectedReady?XpbdStatus.Ready:XpbdStatus.CollisionUnproven,result.Status);
        if(!expectedReady)
        {
            Assert.Equal(before,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
            // Failed terrain/foot composition must not consume the foot identity,
            // fractional clock or target state. The same interval can retry safe.
            feet=Motion(.04f);
            Assert.Equal(XpbdStatus.Ready,solver.AdvanceWithFeet(XpbdCloth.FixedStep,scene,feet,feet.After.SampledAt,inputs).Status);
        }
        var accepted=solver.Capture().Positions.ToArray();
        Assert.True(Vector3.Distance(accepted[2],proposal[2])>.001f,"A terrain repair must actually occur.");
        work=new(100_000);
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.Sweep(before,accepted,[0,1,2],scene,.002f,ref work));
        Assert.Equal(SweepVerdict.Clear,FootCapsuleContacts.Sweep(before,accepted,[0,1,2],feet.Before.Capsules,feet.After.Capsules,.002f,ref work));
    }

    [Theory]
    [InlineData(0f)][InlineData(.61f)][InlineData(1.57f)]
    public void ActualStairCornerChordIsRepairedThenIndependentlyCertified(float yaw)
    {
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
        Vector3 T(Vector3 p)=>Vector3.Transform(p,rotation);
        Vector3[] before=[T(new(.47777557f,.445273f,-.22968818f)),T(new(.26278365f,.3852652f,.009999165f)),T(new(.50294024f,.45551535f,.013261441f))];
        Vector3[] after=[T(new(.46335495f,.43446007f,-.23022887f)),T(new(.24765293f,.37745076f,.009570875f)),T(new(.48914185f,.4420544f,.012655333f))];
        var scene=new MeasuredTriangleScene(1,[new(T(new(.5f,.45000002f,0)),T(new(.5f,.3f,.375f)),T(new(.5f,.45000002f,.375f)))]);
        var original=after.ToArray();var work=new CollisionWork(100_000);var travel=new float[3];
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear(after,[0,1,2],scene,.002f,ref work));
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.Sweep(before,after,[0,1,2],scene,.002f,ref work,out var hint));
        Assert.True(TriangleContacts.RespondToSweep(before,after,[1,1,1],travel,hint,.004f,ref work));
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear(after,[0,1,2],scene,.002f,ref work));
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.Sweep(before,after,[0,1,2],scene,.002f,ref work));
        for(var i=0;i<3;i++)Assert.InRange(Vector3.Distance(original[i],after[i]),0,.025f);
    }

    [Fact]
    public void BudgetAndPinnedWitnessCannotAuthorizeOrMoveTheEndpoint()
    {
        Vector3[] before=[new(-1,.01f,0),new(0,.01f,1),new(1,.01f,0)];
        Vector3[] after=[new(-1,.001f,0),new(0,.001f,1),new(1,.001f,0)];
        var original=after.ToArray();var hint=new SweptContactHint(0,1,2,new(new(-2,0,-2),new(0,0,2),new(2,0,-2)),.8);
        var work=new CollisionWork(0);
        Assert.False(TriangleContacts.RespondToSweep(before,after,[1,1,1],new float[3],hint,.004f,ref work));
        Assert.Equal(original,after);
        work=new(100);
        Assert.False(TriangleContacts.RespondToSweep(before,after,[0,0,0],new float[3],hint,.004f,ref work));
        Assert.Equal(original,after);
    }

    [Theory]
    [InlineData(0d)][InlineData(double.NaN)][InlineData(1.01d)][InlineData(.00001d)]
    public void InvalidOrExcessivelyEarlyHintNeverMakesAnUnboundedCorrection(double time)
    {
        Vector3[] before=[new(-1,.003f,0),new(0,.003f,1),new(1,.003f,0)];
        Vector3[] after=[new(-1,-.1f,0),new(0,-.1f,1),new(1,-.1f,0)];
        var original=after.ToArray();var hint=new SweptContactHint(0,1,2,new(new(-2,0,-2),new(0,0,2),new(2,0,-2)),time);
        var work=new CollisionWork(100);var travel=new float[3];
        Assert.False(TriangleContacts.RespondToSweep(before,after,[1,1,1],travel,hint,.004f,ref work));
        Assert.Equal(original,after);Assert.Equal(new float[3],travel);
    }

    [Fact]
    public void AccumulatedCorrectionLimitDoesNotClampAndPretendSuccess()
    {
        Vector3[] before=[new(-1,.01f,0),new(0,.01f,1),new(1,.01f,0)];
        Vector3[] after=[new(-1,.001f,0),new(0,.001f,1),new(1,.001f,0)];
        var original=after.ToArray();var hint=new SweptContactHint(0,1,2,new(new(-2,0,-2),new(0,0,2),new(2,0,-2)),.8);
        var work=new CollisionWork(100);float[] travel=[.05f,.05f,.05f];
        Assert.False(TriangleContacts.RespondToSweep(before,after,[1,1,1],travel,hint,.004f,ref work));
        Assert.Equal(original,after);Assert.Equal(new[]{.05f,.05f,.05f},travel);
    }
}
