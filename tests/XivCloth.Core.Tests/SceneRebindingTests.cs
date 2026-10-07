using System.Numerics;

namespace XivCloth.Core.Tests;

public sealed class SceneRebindingTests
{
    private static MeasuredTriangle Horizontal(float y,float extent=4)=>
        new(new(-extent,y,-extent),new(0,y,extent),new(extent,y,-extent));

    private static XpbdDefinition Definition()=>new(
        [new(-1,1,-1),new(0,1,1),new(1,1,-1)],
        [new(0,0),new(.5f,1),new(1,0)],[0,1,2],[1,1,1],
        [new(0,1,MaterialEdge.Stretch),new(1,2,MaterialEdge.Stretch),new(2,0,MaterialEdge.Stretch)]);

    [Fact]
    public void RebindingAdmitsInitialPoseWithoutAdvancingOrDisplacingIt()
    {
        var solver=new XpbdCloth(Definition());var original=solver.Capture();
        var result=solver.RebindScene(new(17,[Horizontal(-2)]));
        Assert.Equal(XpbdStatus.Ready,result.Status);
        Assert.Equal(0,result.Substeps);Assert.Equal(0,result.ConstraintSolves);
        Assert.Equal(0,result.InputWork);Assert.Equal(0,result.DroppedSeconds);
        Assert.Equal(original.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(17,solver.Capture().SceneGeneration);
        Assert.False(solver.Capture().EndpointInterpolationAllowed);
    }

    [Fact]
    public void RebindingEquivalentTerrainPreservesMomentumAndFractionalClockExactly()
    {
        var definition=Definition();var settings=new XpbdSettings{Gravity=new(2,-9,1)};
        var control=new XpbdCloth(definition,settings);var solver=new XpbdCloth(definition,settings);
        var before=new MeasuredTriangleScene(1,[Horizontal(-2)]);
        var after=new MeasuredTriangleScene(2,[Horizontal(-2)]);
        var input=new XpbdStepInputs(definition,[new(.2f,.1f,0),new(.2f,.1f,0),new(.2f,.1f,0)]);
        for(var i=0;i<24;i++)
        {
            Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,before,input).Status);
            Assert.Equal(XpbdStatus.Ready,control.Advance(XpbdCloth.FixedStep,before,input).Status);
        }
        solver.Advance(XpbdCloth.FixedStep*.25,before);
        control.Advance(XpbdCloth.FixedStep*.25,before);
        var unchanged=solver.Capture().Positions.ToArray();
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(after).Status);
        Assert.Equal(unchanged,solver.Capture().Positions.ToArray());
        var next=solver.Advance(XpbdCloth.FixedStep*.75,after,input);
        Assert.Equal(XpbdStatus.Ready,next.Status);Assert.Equal(1,next.Substeps);
        Assert.Equal(XpbdStatus.Ready,control.Advance(XpbdCloth.FixedStep*.75,before,input).Status);
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.True(Vector3.Distance(unchanged[0],solver.Capture().Positions[0])>.01f);
        Assert.Equal(2,solver.SceneGeneration);
        // Old publications retain their own association; rebinding does not
        // mutate pose arrays or retroactively rewrite previous snapshots.
        var publication=solver.Capture();
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(new(3,[Horizontal(-2)])).Status);
        Assert.Equal(2,publication.SceneGeneration);
    }

    [Fact]
    public void BuriedCandidateRefusalPreservesOldSceneVelocityAndClock()
    {
        var definition=Definition();var solver=new XpbdCloth(definition);var control=new XpbdCloth(definition);
        var oldScene=new MeasuredTriangleScene(5,[Horizontal(-2)]);
        foreach(var cloth in new[]{solver,control})
        {
            Assert.Equal(XpbdStatus.Ready,cloth.Advance(3.5*XpbdCloth.FixedStep,oldScene).Status);
        }
        var before=solver.Capture();
        var denied=new MeasuredTriangleScene(6,[Horizontal(2)]);
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.RebindScene(denied).Status);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(5,solver.SceneGeneration);
        Assert.Equal(XpbdStatus.InvalidContactOrState,solver.Advance(XpbdCloth.FixedStep,denied).Status);
        foreach(var cloth in new[]{solver,control})
            Assert.Equal(1,cloth.Advance(XpbdCloth.FixedStep*.5,oldScene).Substeps);
        Assert.Equal(control.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void NewObstacleThroughFaceInteriorRefusesDespiteClearVertices()
    {
        var solver=new XpbdCloth(Definition(),new(){Gravity=default});
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(new(1,[])).Status);
        var smallWall=new MeasuredTriangle(new(0,.9f,-.05f),new(0,1.1f,-.05f),new(0,1,.05f));
        var denied=new MeasuredTriangleScene(2,[smallWall],twoSided:true);
        Assert.Equal(0,denied.MaximumVertexPenetration(solver.Capture().Positions));
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.RebindScene(denied).Status);
        Assert.Equal(1,solver.SceneGeneration);
    }

    [Fact]
    public void BudgetExhaustionDoesNotReplacePreviousSceneOrConsumeTime()
    {
        var solver=new XpbdCloth(Definition(),new(){CollisionWorkLimit=0});
        var empty=new MeasuredTriangleScene(8,[]);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep*.5,empty).Status);
        var before=solver.Capture();
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,solver.RebindScene(new(9,[Horizontal(-2)])).Status);
        Assert.Equal(8,solver.SceneGeneration);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(1,solver.Advance(XpbdCloth.FixedStep*.5,empty).Substeps);
    }

    [Fact]
    public void OrdinaryAdvanceStillRejectsUnboundGeneration()
    {
        var solver=new XpbdCloth(Definition());
        var oldScene=new MeasuredTriangleScene(1,[]);var next=new MeasuredTriangleScene(2,[]);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,oldScene).Status);
        var before=solver.Capture();
        Assert.Equal(XpbdStatus.InvalidContactOrState,solver.Advance(XpbdCloth.FixedStep,next).Status);
        Assert.Equal(before.Positions.ToArray(),solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(next).Status);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,next).Status);
    }

    [Fact]
    public void StaticRebindingDoesNotPretendToSweepAMovingObstacleBetweenSnapshots()
    {
        var solver=new XpbdCloth(Definition(),new(){Gravity=default});
        var below=new MeasuredTriangleScene(1,[Horizontal(.5f)],twoSided:true);
        var above=new MeasuredTriangleScene(2,[Horizontal(1.5f)],twoSided:true);
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(below).Status);
        // Both static endpoints are clear; the obstacle's hypothetical path
        // between them crosses the cloth. A moving-body caller must NOT use
        // static rebinding as a substitute for a temporal collision contract.
        Assert.Equal(XpbdStatus.Ready,solver.RebindScene(above).Status);
        Assert.Equal(Definition().Rest,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void NullSceneThrowsWithoutChangingAssociation()
    {
        var solver=new XpbdCloth(Definition());solver.RebindScene(new(3,[]));
        Assert.Throws<ArgumentNullException>(()=>solver.RebindScene(null!));
        Assert.Equal(3,solver.SceneGeneration);
    }
}
