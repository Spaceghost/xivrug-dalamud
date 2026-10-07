using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class TriangleCollisionTests
{
    private static MeasuredTriangle Shift(MeasuredTriangle t,Vector3 p)=>new(t.A+p,t.B+p,t.C+p);
    private static MeasuredTriangle Horizontal(float y,float size=1)=>new(new(-size,y,-size),new(0,y,size),new(size,y,-size));
    private static XpbdDefinition Definition(MeasuredTriangle t)=>new([t.A,t.B,t.C],new Vector2[3],[0,1,2],[1,1,1],
        [new(0,1,0),new(1,2,0),new(2,0,0)]);
    private static SweepVerdict Check(MeasuredTriangle a,MeasuredTriangle b,MeasuredTriangle c,double clearance=.002)
    {var work=new CollisionWork(100_000);return TriangleSweep.Check(a,b,c,clearance,ref work);}

    [Fact]
    public void FaceInteriorTunnelsThroughTinyFaceAlthoughAllVertexTrajectoriesMiss()
    {
        var above=Horizontal(.1f);var below=Horizontal(-.1f);var obstacle=Horizontal(0,.05f);
        Assert.Equal(SweepVerdict.Clear,Check(above,above,obstacle));
        Assert.Equal(SweepVerdict.Clear,Check(below,below,obstacle));
        Assert.Equal(SweepVerdict.Unproven,Check(above,below,obstacle));
        foreach(var p in new[]{above.A,above.B,above.C})Assert.True(Math.Abs(p.X)>.05||Math.Abs(p.Z)>.05);
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public void SkinnySlopedStaticFaceCannotBeCrossed(int axis)
    {
        Vector3 Transform(Vector3 p)
        {
            p=Vector3.Transform(p,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.37f));
            return (axis==0?p:axis==1?new(p.Y,p.Z,p.X):new(p.Z,p.X,p.Y))+new Vector3(300,-120,700);
        }
        MeasuredTriangle Map(MeasuredTriangle t)=>new(Transform(t.A),Transform(t.B),Transform(t.C));
        var obstacle=Map(new(new(-1,0,-.0003f),new(0,0,.0003f),new(1,0,-.0003f)));
        Assert.Equal(SweepVerdict.Unproven,Check(Map(Horizontal(.1f,2)),Map(Horizontal(-.1f,2)),obstacle));
        Assert.Equal(SweepVerdict.Clear,Check(Map(Horizontal(.1f,2)),Map(Horizontal(.11f,2)),obstacle));
    }

    [Fact]
    public void EdgeEdgeGrazingIsNotClearEvenWithoutContainedVertices()
    {
        var a=new MeasuredTriangle(new(-1,0,0),new(1,0,0),new(0,0,-1));
        var b=new MeasuredTriangle(new(0,-1,0),new(0,1,0),new(0,0,1));
        Assert.Equal(SweepVerdict.Unproven,Check(a,a,b));
        Assert.Equal(SweepVerdict.Clear,Check(Shift(a,new(0,0,-.01f)),Shift(a,new(0,0,-.02f)),b));
    }

    [Fact]
    public void CoplanarSeparatedPolygonsWithOverlappingAabbsHaveAnInPlaneWitness()
    {
        var a=new MeasuredTriangle(new(0,0,0),new(2,0,0),new(0,0,2));
        var b=new MeasuredTriangle(new(1.5f,0,1.5f),new(3.5f,0,1.5f),new(1.5f,0,3.5f));
        Assert.Equal(SweepVerdict.Clear,Check(a,a,b));
        Assert.Equal(SweepVerdict.Unproven,Check(a,a,Shift(b,new(-1,0,-1))));
    }

    [Fact]
    public void TangentialMotionKeepsConservativeHalfThicknessClearance()
    {
        var a=Horizontal(.004f,.1f);var floor=Horizontal(0,4);
        Assert.Equal(SweepVerdict.Clear,Check(a,Shift(a,new(1,0,0)),floor,.002));
        // Exact configured response thickness is NOT the published certificate.
        Assert.Equal(SweepVerdict.Unproven,Check(a,a,floor,.0041));
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(6)]
    public void ExhaustionNeverBecomesAClearVerdict(int limit)
    {
        var work=new CollisionWork(limit);
        var verdict=TriangleSweep.Check(Horizontal(.1f),Horizontal(-.1f),Horizontal(0),.002,ref work);
        Assert.Equal(SweepVerdict.BudgetExceeded,verdict);Assert.Equal(limit,work.Used);
    }

    [Fact]
    public void InvalidInputsAreNotClear()
    {
        var work=new CollisionWork(50);
        Assert.Equal(SweepVerdict.Invalid,TriangleSweep.Check(default,Horizontal(0),Horizontal(1),.002,ref work));
        Assert.Equal(SweepVerdict.Invalid,TriangleSweep.Check(Horizontal(0),Horizontal(0),Horizontal(1),double.NaN,ref work));
        Assert.Equal(0,work.Used);
    }

    [Fact]
    public void ExplicitTwoSidedSheetsAllowTheSpaceBetweenStackedLayers()
    {
        var t=Horizontal(.5f,.1f);var solver=new XpbdCloth(Definition(t),new(){Gravity=default});
        var scene=new MeasuredTriangleScene(3,[Horizontal(0,2),Horizontal(1,2)],twoSided:true);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(1d/60,scene).Status);
        Assert.Equal(new[]{t.A,t.B,t.C},solver.Capture().Positions.ToArray());
        // Explicit synthetic thin-sheet policy, not authorization of unknown
        // overlapping layers from a game model or collision-mesh heightfield.
    }

    [Fact]
    public void FiniteFaceProofDoesNotCertifyExtrudedOneSidedSolidContainment()
    {
        var support=Horizontal(0,.2f);var left=Shift(Horizontal(-.1f,.05f),new(-1,0,0));
        var right=Shift(left,new(2,0,0));
        Assert.Equal(SweepVerdict.Clear,Check(left,right,support));
        // It passes below the finite face without crossing it. The two endpoint
        // support checks pass, but the infinite blocked prism is entered in the
        // middle. Real solid stairs need explicit risers/closed boundaries.
        var scene=new MeasuredTriangleScene(1,[support]);var work=new CollisionWork(100);
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear([left.A,left.B,left.C],[0,1,2],scene,.002f,ref work));
        Assert.Equal(SweepVerdict.Clear,TriangleContacts.OneSidedClear([right.A,right.B,right.C],[0,1,2],scene,.002f,ref work));
        var middle=Horizontal(-.1f,.05f);
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.OneSidedClear([middle.A,middle.B,middle.C],[0,1,2],scene,.002f,ref work));
    }

    [Fact]
    public void PublicAdvanceRefusesThinWallTunnellingAndRollsBackEarlierSubstepVelocityAndClock()
    {
        var rest=new MeasuredTriangle(new(-1,-1,-1),new(-1,0,1),new(-1,1,-1));
        var settings=new XpbdSettings{Gravity=new(30,0,0),Drag=0,MaximumVelocity=20,Iterations=1};
        var control=new XpbdCloth(Definition(rest),settings);
        for(var i=0;i<20;i++)Assert.Equal(XpbdStatus.Ready,control.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty).Status);
        var frame20=control.Capture().Positions.ToArray();
        Assert.Equal(XpbdStatus.Ready,control.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty).Status);
        var frame21=control.Capture().Positions.ToArray();var wallX=(frame20[0].X+frame21[0].X)*.5f;
        var wall=new MeasuredTriangle(new(wallX,-.05f,-.05f),new(wallX,0,.05f),new(wallX,.05f,-.05f));
        Assert.True(wallX-frame20[0].X>.02f&&frame21[0].X-wallX>.02f);
        var scene=new MeasuredTriangleScene(1,[wall],twoSided:true);var solver=new XpbdCloth(Definition(rest),settings);
        for(var i=0;i<19;i++)Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,scene).Status);
        solver.Advance(XpbdCloth.FixedStep/2,scene);var before=solver.Capture().Positions.ToArray();
        var failed=solver.Advance(2*XpbdCloth.FixedStep,scene);
        Assert.Equal(XpbdStatus.CollisionUnproven,failed.Status);Assert.Equal(0,failed.Substeps);
        Assert.True(failed.SweepNodes>0&&failed.TrianglePairs>0&&failed.ConstraintSolves==6);
        Assert.Equal(before,solver.Capture().Positions.ToArray());
        // Same immutable geometry generation. Half a step must consume the
        // previous fractional remainder; no failed-step velocity may survive.
        Assert.Equal(1,solver.Advance(XpbdCloth.FixedStep/2,scene).Substeps);
        Assert.Equal(frame20,solver.Capture().Positions.ToArray());
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.Advance(XpbdCloth.FixedStep,scene).Status);
        Assert.Equal(frame20,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void InitialIntersectingFacesRefuseWithoutInventingASafeSide()
    {
        var rest=new MeasuredTriangle(new(-1,0,0),new(1,0,0),new(0,0,-1));
        var obstacle=new MeasuredTriangle(new(0,-1,0),new(0,1,0),new(0,0,1));
        var solver=new XpbdCloth(Definition(rest),new(){Gravity=default});
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.Advance(1d/120,new(1,[obstacle],true)).Status);
        Assert.Equal(new[]{rest.A,rest.B,rest.C},solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
    }

    [Fact]
    public void PublicationExplicitlyForbidsUnprovedEndpointInterpolation()
    {
        var solver=new XpbdCloth(Definition(Horizontal(1)),new(){Gravity=default});
        Assert.False(solver.Capture().EndpointInterpolationAllowed);Assert.Equal(0,solver.Capture().SceneGeneration);
        Assert.Equal(XpbdStatus.Ready,solver.Advance(1d/60,new(17,[])).Status);
        Assert.False(solver.Capture().EndpointInterpolationAllowed);Assert.Equal(17,solver.Capture().SceneGeneration);
    }

    [Theory]
    [InlineData(0f)][InlineData(9990f)]
    public void BvhCandidatesMatchLinearBoundsIncludingTouchingSkinnyAndLargeCoordinates(float offset)
    {
        var origin=new Vector3(offset);var triangles=new List<MeasuredTriangle>();var random=new Random(7301);
        // Preserve a representable skinny width at large coordinates rather
        // than silently rounding the fixture itself to a degenerate triangle.
        var width=Math.Max(.0001f,2*(MathF.BitIncrement(offset)-offset));
        triangles.Add(Shift(new(new(0,0,0),new(1,0,width),new(1,0,0)),origin));
        for(var i=1;i<128;i++)triangles.Add(Shift(Horizontal((float)random.NextDouble(),.02f),origin+new Vector3((float)random.NextDouble()*2,0,(float)random.NextDouble()*2)));
        var scene=new MeasuredTriangleScene(1,triangles.ToArray(),true);var results=new int[128];
        var queries=new List<Box3>{new(origin,origin),new(origin+new Vector3(1,0,0),origin+new Vector3(1,0,0)),Box3.Triangle(triangles[0]).Expand(.002f)};
        for(var i=0;i<60;i++)
        {
            var p=origin+new Vector3((float)random.NextDouble()*2,(float)random.NextDouble(),(float)random.NextDouble()*2);
            queries.Add(new Box3(p,p+new Vector3(.15f)).Expand(.002f));
        }
        foreach(var query in queries)
        {
            var work=new CollisionWork(1000);var count=scene.Query(query,results,ref work);
            var expected=triangles.Select((t,i)=>(t,i)).Where(p=>query.Intersects(Box3.Triangle(p.t))).Select(p=>p.i).ToArray();
            Assert.Equal(expected,results[..count]);Assert.InRange(work.TreeNodes,1,255);
        }
    }

    [Fact]
    public void PrunedSweepsAgreeWithEveryTriangleLinearProof()
    {
        var obstacles=Enumerable.Range(0,128).Select(i=>Shift(Horizontal(0,.1f),new Vector3(i*.2f,0,0))).ToArray();
        var scene=new MeasuredTriangleScene(1,obstacles,true);
        foreach(var height in new[]{-.1f,.1f})
        {
            var before=Horizontal(.1f,.05f);var after=Horizontal(height,.05f);var reference=SweepVerdict.Clear;
            foreach(var obstacle in obstacles)
            {var verdict=Check(before,after,obstacle);if(verdict!=SweepVerdict.Clear){reference=verdict;break;}}
            var work=new CollisionWork(100_000);
            Assert.Equal(reference,TriangleContacts.Sweep([before.A,before.B,before.C],[after.A,after.B,after.C],[0,1,2],scene,.002f,ref work));
        }
    }

    [Fact]
    public void RemoteTrianglesArePrunedAndCollisionBudgetRefusalIsTransactional()
    {
        var t=Horizontal(1,.1f);var obstacles=Enumerable.Range(0,128).Select(i=>Shift(Horizontal(0,.1f),new Vector3(100+i,0,100))).ToArray();
        var scene=new MeasuredTriangleScene(1,obstacles,true);
        var solver=new XpbdCloth(Definition(t),new(){Gravity=default});var result=solver.Advance(XpbdCloth.FixedStep,scene);
        Assert.Equal(XpbdStatus.Ready,result.Status);Assert.Equal(0,result.SweepNodes);Assert.Equal(0,result.TrianglePairs);
        // Ten query/containment checks plus one counted owner initialization
        // and the first immutable-tree refill; no actual obstacle pair survives.
        Assert.Equal(12,result.BroadphaseNodes);
        var limited=new XpbdCloth(Definition(t),new(){Gravity=default,CollisionWorkLimit=1});
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,limited.Advance(XpbdCloth.FixedStep,scene).Status);
        Assert.Equal(new[]{t.A,t.B,t.C},limited.Capture().Positions.ToArray());Assert.Equal(0,limited.SceneGeneration);
    }

    [Theory]
    [InlineData(0f,0f)][InlineData(.3f,.15f)]
    public void ActualFifteenCentimetreRiserFaceBlocksCrossing(float x,float baseY)
    {
        var riser=new MeasuredTriangle(new(x,baseY,-1),new(x,baseY+.15f,1),new(x,baseY+.15f,-1));
        var face=new MeasuredTriangle(new(x-.1f,baseY+.09f,-.05f),new(x-.1f,baseY+.11f,.05f),new(x-.1f,baseY+.13f,-.05f));
        Assert.Equal(SweepVerdict.Unproven,Check(face,Shift(face,new(.2f,0,0)),riser));
        Assert.Equal(SweepVerdict.Clear,Check(Shift(face,new(0,.3f,0)),Shift(face,new(.2f,.3f,0)),riser));
    }
}
