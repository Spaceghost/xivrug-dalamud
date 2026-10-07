using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class XpbdTests(ITestOutputHelper output)
{
    private sealed record Grid(XpbdDefinition Definition,Vector3[] Rest,int Columns,int Rows,DistanceEdge[] Edges);
    private static Grid Sheet(int columns=9,int rows=9,float spacing=.1f,Func<int,int,bool>? pinned=null,float height=1)
    {
        var p=new Vector3[columns*rows];var uv=new Vector2[p.Length];var mass=new float[p.Length];
        var faces=new List<int>();var edges=new List<DistanceEdge>();
        for(var z=0;z<rows;z++)for(var x=0;x<columns;x++)
        {
            var i=z*columns+x;p[i]=new((x-(columns-1)*.5f)*spacing,height,(z-(rows-1)*.5f)*spacing);
            uv[i]=new((float)x/(columns-1),(float)z/(rows-1));mass[i]=pinned?.Invoke(x,z)==true?0:1;
            if(x+1<columns)edges.Add(new(i,i+1,MaterialEdge.Stretch));
            if(z+1<rows)edges.Add(new(i,i+columns,MaterialEdge.Stretch));
            if(x+1<columns&&z+1<rows)
            {
                var b=i+1;var c=i+columns;var d=c+1;
                faces.AddRange([i,c,b,b,c,d]);
                edges.Add(new(i,d,MaterialEdge.Shear));edges.Add(new(b,c,MaterialEdge.Shear));
            }
        }
        return new(new(p,uv,faces.ToArray(),mass,edges.ToArray()),p,columns,rows,edges.ToArray());
    }
    private static void Run(XpbdCloth solver,int frames=360,MeasuredTriangleScene? scene=null)
    {
        for(var i=0;i<frames;i++)Assert.Equal(XpbdStatus.Ready,solver.Advance(1d/120,scene??MeasuredTriangleScene.Empty).Status);
    }
    private static float Stretch(Grid grid,XpbdFrame frame,MaterialEdge kind)
    {
        var maximum=0f;
        foreach(var e in grid.Edges.Where(x=>x.Kind==kind))
        {var rest=Vector3.Distance(grid.Rest[e.A],grid.Rest[e.B]);maximum=Math.Max(maximum,Math.Abs(Vector3.Distance(frame.Positions[e.A],frame.Positions[e.B])/rest-1));}
        return maximum;
    }

    [Fact]
    public void TwoPinnedCornersSagAndMoveInThreeDimensionsWithoutStretchingFreely()
    {
        var grid=Sheet(pinned:(x,z)=>z==0&&(x==0||x==8));var solver=new XpbdCloth(grid.Definition);
        Run(solver);var frame=solver.Capture();
        output.WriteLine($"hanging: centerY={frame.Positions[40].Y:R}, stretch={Stretch(grid,frame,MaterialEdge.Stretch):R}, shear={Stretch(grid,frame,MaterialEdge.Shear):R}");
        Assert.Equal(grid.Rest[0],frame.Positions[0]);Assert.Equal(grid.Rest[8],frame.Positions[8]);
        Assert.True(frame.Positions[40].Y<.8f);
        Assert.True(Math.Abs(frame.Positions[80].Z-grid.Rest[80].Z)>.05f);
        Assert.InRange(Stretch(grid,frame,MaterialEdge.Stretch),0,.05f);
        Assert.InRange(Stretch(grid,frame,MaterialEdge.Shear),0,.05f);
        Assert.True(grid.Definition.StretchCount>0&&grid.Definition.ShearCount>0&&grid.Definition.BendingCount>0);
    }

    [Theory]
    [InlineData(.0f)][InlineData(.4f)][InlineData(-1.1f)][InlineData(2.9f)][InlineData(-2.9f)]
    public void DihedralAnalyticGradientMatchesFiniteDifference(float fold)
    {
        Vector3[] p=[new(-.8f,.1f,.1f),new(.8f*MathF.Cos(fold),.8f*MathF.Sin(fold),-.1f),new(0,0,-.7f),new(0,0,.8f)];
        Assert.True(XpbdDihedral.Evaluate(p[0],p[1],p[2],p[3],out _,out var a,out var b,out var c,out var d));
        Vector3[] gradient=[a,b,c,d];const float epsilon=.0001f;
        for(var i=0;i<4;i++)for(var axis=0;axis<3;axis++)
        {
            var old=p[i];var delta=axis==0?Vector3.UnitX*epsilon:axis==1?Vector3.UnitY*epsilon:Vector3.UnitZ*epsilon;
            p[i]=old+delta;Assert.True(XpbdDihedral.Evaluate(p[0],p[1],p[2],p[3],out var plus,out _,out _,out _,out _));
            p[i]=old-delta;Assert.True(XpbdDihedral.Evaluate(p[0],p[1],p[2],p[3],out var minus,out _,out _,out _,out _));p[i]=old;
            var actual=axis==0?gradient[i].X:axis==1?gradient[i].Y:gradient[i].Z;
            Assert.InRange(Math.Abs(XpbdDihedral.Wrap(plus-minus)/(2*epsilon)-actual),0,.003f);
        }
        Assert.InRange((a+b+c+d).Length(),0,.00001f);
    }

    [Fact]
    public void GenuineHingeBendingResistsADeformationWithAllEdgeLengthsUnchanged()
    {
        Vector3[] rest=[new(-1,0,0),new(1,0,0),new(0,0,-1),new(0,0,1)];
        var definition=new XpbdDefinition(rest,new Vector2[4],[0,2,3,1,3,2],[0,1,0,0],
            [new(0,2,0),new(0,3,0),new(2,3,0),new(1,2,0),new(1,3,0)]);
        var pose=(Vector3[])rest.Clone();pose[1]=new(MathF.Cos(.8f),MathF.Sin(.8f),0);
        var soft=new XpbdCloth(definition,new(){Gravity=default,Bending=false,Drag=3});soft.Reset(pose);
        var bend=new XpbdCloth(definition,new(){Gravity=default,BendCompliance=1e-5f,Drag=3});bend.Reset(pose);
        Run(soft,120);Run(bend,120);
        var s=soft.Capture().Positions[1];var b=bend.Capture().Positions[1];
        Assert.InRange(Math.Abs(s.Y-pose[1].Y),0,.001f);Assert.True(Math.Abs(b.Y)<.02f);
    }

    [Fact]
    public void FixedStepsAreExactlyIndependentOfCallerFrameGrouping()
    {
        var grid=Sheet(7,7,pinned:(x,z)=>z==0);var a=new XpbdCloth(grid.Definition);var b=new XpbdCloth(grid.Definition);
        for(var i=0;i<240;i++)a.Advance(1d/120,MeasuredTriangleScene.Empty);
        for(var i=0;i<120;i++)b.Advance(1d/60,MeasuredTriangleScene.Empty);
        Assert.Equal(a.Capture().Positions.ToArray(),b.Capture().Positions.ToArray());
    }

    [Fact]
    public void PublicationAndDefinitionDoNotAliasInputsOrLaterSimulation()
    {
        var grid=Sheet(5,5,pinned:(x,z)=>z==0);var original=(Vector3[])grid.Rest.Clone();
        var solver=new XpbdCloth(grid.Definition);var published=solver.Capture();var saved=published.Positions.ToArray();
        Array.Fill(grid.Rest,new Vector3(999));Run(solver,20);
        Assert.Equal(original,published.Positions.ToArray());Assert.Equal(saved,published.Positions.ToArray());
        Assert.NotEqual(saved,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void LongFrameDropsDebtAndNeverExceedsSubstepBudget()
    {
        var solver=new XpbdCloth(Sheet(5,5).Definition);
        var result=solver.Advance(1,MeasuredTriangleScene.Empty);
        Assert.Equal(XpbdStatus.Ready,result.Status);Assert.Equal(8,result.Substeps);Assert.True(result.DroppedSeconds>.9);
        Assert.Equal(0,solver.Advance(0,MeasuredTriangleScene.Empty).Substeps);
    }

    [Fact]
    public void WorkBudgetRefusesBeforeMutatingRatherThanSkippingContacts()
    {
        var grid=Sheet(21,21);var solver=new XpbdCloth(grid.Definition,new(){Iterations=16});
        var triangle=new MeasuredTriangle(new(-4,-10,-4),new(4,-10,4),new(4,-10,-4));
        var scene=new MeasuredTriangleScene(1,Enumerable.Repeat(triangle,128).ToArray());
        var result=solver.Advance(1d/15,scene);
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,result.Status);Assert.Equal(0,result.Substeps);Assert.Equal(0,result.ContactChecks);
        Assert.Equal(grid.Rest,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void ResetClearsVelocityClockAndSceneGeneration()
    {
        var grid=Sheet(5,5,pinned:(x,z)=>z==0);var solver=new XpbdCloth(grid.Definition);Run(solver,30);
        Assert.Equal(XpbdStatus.InvalidContactOrState,solver.Advance(1d/120,new(2,[])).Status);
        solver.Reset();Assert.Equal(0,solver.SceneGeneration);Assert.Equal(grid.Rest,solver.Capture().Positions.ToArray());
        var fresh=new XpbdCloth(grid.Definition);Run(solver,30);Run(fresh,30);
        Assert.Equal(fresh.Capture().Positions.ToArray(),solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void ExplicitVerticalTriangleContactMovesXNotJustHeight()
    {
        var grid=Sheet(3,3,height:.2f);var solver=new XpbdCloth(grid.Definition,new(){Gravity=new(9,0,0),Bending=false});
        // Plane at x=.25, outward normal -X. Deliberately finite triangle pair.
        MeasuredTriangle[] walls=[new(new(.25f,-1,-1),new(.25f,1,1),new(.25f,1,-1)),
            new(new(.25f,-1,-1),new(.25f,-1,1),new(.25f,1,1))];
        var scene=new MeasuredTriangleScene(1,walls);Run(solver,120,scene);var frame=solver.Capture();
        Assert.True(frame.Positions.ToArray().Max(p=>p.X)<=.24601f);
        Assert.True(frame.Positions[0].X>grid.Rest[0].X+.1f);
        Assert.InRange(scene.MaximumVertexPenetration(frame.Positions),0,.00001f);
    }

    [Fact]
    public void FiniteTriangleDoesNotBecomeAnInfiniteFloorPlane()
    {
        var grid=Sheet(3,3,height:.5f);var solver=new XpbdCloth(grid.Definition);
        var scene=new MeasuredTriangleScene(1,[new(new(10,0,10),new(11,0,11),new(11,0,10))]);
        Run(solver,60,scene);Assert.True(solver.Capture().Positions[0].Y<0);
    }

    [Fact]
    public void ExcessiveInitialPenetrationFailsTransactionally()
    {
        var grid=Sheet(3,3,height:-.2f);var solver=new XpbdCloth(grid.Definition);
        var scene=new MeasuredTriangleScene(1,[new(new(-3,0,-3),new(3,0,3),new(3,0,-3)),new(new(-3,0,-3),new(-3,0,3),new(3,0,3))]);
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.Advance(1d/120,scene).Status);
        Assert.Equal(grid.Rest,solver.Capture().Positions.ToArray());
    }

    [Theory]
    [InlineData(.15f)][InlineData(.2f)]
    public void DrapeAroundActualSyntheticTreadAndRiserAllowsHorizontalMaterialTravel(float height)
    {
        var grid=Sheet(17,5,.075f,pinned:(x,z)=>x==16,height:height+.005f);
        var solver=new XpbdCloth(grid.Definition,new(){Iterations=12,BendCompliance=.3f,Drag=4});
        var scene=Stair(height);Run(solver,600,scene);var frame=solver.Capture();var points=frame.Positions.ToArray();
        output.WriteLine($"stair: max X travel={points.Zip(grid.Rest,(a,b)=>Math.Abs(a.X-b.X)).Max():R}, stretch={Stretch(grid,frame,MaterialEdge.Stretch):R}, penetration={scene.MaximumVertexPenetration(frame.Positions):R}");
        for(var x=0;x<17;x++)output.WriteLine($"stair middle row {x}: {points[34+x]}");
        Assert.InRange(scene.MaximumVertexPenetration(frame.Positions),0,.00002f);
        Assert.Contains(points,p=>p.X<-.02f&&p.Y<.05f);
        Assert.Contains(points,p=>p.X>.05f&&p.Y>height-.001f&&p.Y<height+.03f);
        Assert.Contains(points,p=>Math.Abs(p.X)<.08f&&p.Y>.025f&&p.Y<height-.02f);
        Assert.True(points.Zip(grid.Rest,(a,b)=>Math.Abs(a.X-b.X)).Max()>.05f);
        Assert.InRange(Stretch(grid,frame,MaterialEdge.Stretch),0,.06f);
    }
    private static MeasuredTriangleScene Stair(float height)
    {
        var t=new List<MeasuredTriangle>();
        void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d){t.Add(new(a,b,c));t.Add(new(a,c,d));}
        Quad(new(-2,0,-2),new(-2,0,2),new(0,0,2),new(0,0,-2));
        Quad(new(0,0,-2),new(0,0,2),new(0,height,2),new(0,height,-2));
        Quad(new(0,height,-2),new(0,height,2),new(2,height,2),new(2,height,-2));
        return new(1,t.ToArray());
    }

    [Fact]
    public void ShearConstraintResistsRhombusWithUnchangedStretchEdges()
    {
        var grid=Sheet(2,2,1,pinned:(x,z)=>z==0,height:0);
        var pose=(Vector3[])grid.Rest.Clone();var offset=new Vector3(MathF.Sin(.5f),0,MathF.Cos(.5f)-1);
        pose[2]+=offset;pose[3]+=offset;
        var soft=new XpbdCloth(grid.Definition,new(){Gravity=default,Bending=false,ShearCompliance=1,Drag=3});
        var stiff=new XpbdCloth(grid.Definition,new(){Gravity=default,Bending=false,ShearCompliance=1e-7f,Drag=3});
        soft.Reset(pose);stiff.Reset(pose);Run(soft,180);Run(stiff,180);
        Assert.True(Math.Abs(soft.Capture().Positions[2].X-grid.Rest[2].X)>.2f);
        Assert.InRange(Math.Abs(stiff.Capture().Positions[2].X-grid.Rest[2].X),0,.005f);
    }

    [Fact]
    public void PinnedParticleInsideColliderIsRefusedNotSilentlyMovedOrIgnored()
    {
        var grid=Sheet(3,3,pinned:(x,z)=>true,height:-.01f);var solver=new XpbdCloth(grid.Definition);
        var scene=new MeasuredTriangleScene(1,[new(new(-3,0,-3),new(3,0,3),new(3,0,-3)),new(new(-3,0,-3),new(-3,0,3),new(3,0,3))]);
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.Advance(1d/120,scene).Status);
        Assert.Equal(grid.Rest,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void WarmSimulationUsesNoPerSubstepManagedAllocations()
    {
        var solver=new XpbdCloth(Sheet(5,5,pinned:(x,z)=>z==0).Definition);Run(solver,100);
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<100;i++)solver.Advance(1d/120,MeasuredTriangleScene.Empty);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }

    [Fact]
    public void AccumulatedMultiplierMatchesSingleConstraintXpbdEquationAcrossIterations()
    {
        Vector3[] rest=[new(1,0,0),default,new(0,1,0)];
        var definition=new XpbdDefinition(rest,new Vector2[3],[0,1,2],[1,0,0],[new(0,1,MaterialEdge.Stretch)]);
        var pose=(Vector3[])rest.Clone();pose[0]=new(1.5f,0,0);
        foreach(var iterations in new[]{1,4,16})
        {
            var solver=new XpbdCloth(definition,new(){Gravity=default,Drag=0,StretchCompliance=.001f,Iterations=iterations});
            solver.Reset(pose);Assert.Equal(XpbdStatus.Ready,solver.Advance(XpbdCloth.FixedStep,MeasuredTriangleScene.Empty).Status);
            var expected=1.5-.5/(1+.001/(XpbdCloth.FixedStep*XpbdCloth.FixedStep));
            Assert.InRange(Math.Abs(solver.Capture().Positions[0].X-expected),0,.000001);
        }
    }

    [Fact]
    public void BuriedFaceInteriorIsRefusedEvenWhenEveryVertexIsClear()
    {
        Vector3[] rest=[new(-1,-.01f,-1),new(0,-.01f,1),new(1,-.01f,-1)];
        var definition=new XpbdDefinition(rest,new Vector2[3],[0,1,2],[1,1,1],
            [new(0,1,0),new(1,2,0),new(2,0,0)]);
        var scene=new MeasuredTriangleScene(1,[new(new(-.1f,0,-.1f),new(0,0,.1f),new(.1f,0,-.1f))]);
        var solver=new XpbdCloth(definition,new(){Gravity=default});
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.Advance(XpbdCloth.FixedStep,scene).Status);
        var frame=solver.Capture();Assert.Equal(0,scene.MaximumVertexPenetration(frame.Positions));
        Assert.True((frame.Positions[0]+frame.Positions[1]+frame.Positions[2]).Y/3<0);
        Assert.Equal(rest,frame.Positions.ToArray());
        // An initially buried face must be refused, not teleported through the
        // finite floor merely because its three corners miss the footprint.
    }

    [Fact]
    public void DegenerateResetPreservesPreviousAcceptedPose()
    {
        var grid=Sheet(3,3);var solver=new XpbdCloth(grid.Definition);
        Assert.Throws<ArgumentException>(()=>solver.Reset(new Vector3[9]));
        Assert.Equal(grid.Rest,solver.Capture().Positions.ToArray());
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public void SkinnyAdmittedTriangleCannotSilentlyDisappearFromContacts(int rotation)
    {
        Vector3 Rotate(Vector3 p)=>rotation==0?p:rotation==1?new(p.Y,p.Z,p.X):new(p.Z,p.X,p.Y);
        var scene=new MeasuredTriangleScene(1,[new(Rotate(default),Rotate(new(1,0,.0001f)),Rotate(new(1,0,0)))]);
        var below=Rotate(new(.75f,-.01f,.000025f));
        Assert.InRange(scene.MaximumVertexPenetration([below]),.009999f,.010001f);
        Vector3[] points=[below,Rotate(new(.75f,.01f,.001f)),Rotate(new(.85f,.01f,.001f))];
        var definition=new XpbdDefinition(points,new Vector2[3],[0,1,2],[1,0,0],[new(1,2,0)]);
        var solver=new XpbdCloth(definition,new(){Gravity=default,Bending=false});
        Assert.Equal(XpbdStatus.CollisionUnproven,solver.Advance(XpbdCloth.FixedStep,scene).Status);
        Assert.Equal(points,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void LaterSubstepFailureRestoresVelocityAndFractionalClockForRetry()
    {
        var grid=Sheet(3,3,height:5);var settings=new XpbdSettings{Gravity=new(0,-30,0),Drag=0,MaximumVelocity=20};
        var scene=new MeasuredTriangleScene(1,[new(new(-3,2.4f,-3),new(3,2.4f,3),new(3,2.4f,-3)),
            new(new(-3,2.4f,-3),new(-3,2.4f,3),new(3,2.4f,3))]);
        var tested=new XpbdCloth(grid.Definition,settings);var control=new XpbdCloth(grid.Definition,settings);
        Run(tested,48,scene);Run(control,48,scene);
        tested.Advance(XpbdCloth.FixedStep/2,scene);control.Advance(XpbdCloth.FixedStep/2,scene);
        var before=tested.Capture().Positions.ToArray();var failed=tested.Advance(2*XpbdCloth.FixedStep,scene);
        Assert.Equal(XpbdStatus.InvalidContactOrState,failed.Status);
        Assert.True(failed.ConstraintSolves>settings.Iterations*(grid.Definition.StretchCount+grid.Definition.ShearCount+grid.Definition.BendingCount));
        Assert.Equal(before,tested.Capture().Positions.ToArray());
        var retry=tested.Advance(XpbdCloth.FixedStep/2,scene);var expected=control.Advance(XpbdCloth.FixedStep/2,scene);
        Assert.Equal(expected,retry);Assert.Equal(1,retry.Substeps);Assert.Equal(XpbdStatus.Ready,retry.Status);
        Assert.Equal(control.Capture().Positions.ToArray(),tested.Capture().Positions.ToArray());
    }

    [Theory]
    [InlineData(double.NaN)][InlineData(-.1)][InlineData(2)]
    public void InvalidTimeNeverMutatesState(double time)
    {
        var grid=Sheet(3,3);var solver=new XpbdCloth(grid.Definition);
        Assert.Throws<ArgumentOutOfRangeException>(()=>solver.Advance(time,MeasuredTriangleScene.Empty));
        Assert.Equal(grid.Rest,solver.Capture().Positions.ToArray());
    }

    [Fact]
    public void RejectsNonFiniteDegenerateOverBudgetAndNonmanifoldInputs()
    {
        Assert.Throws<ArgumentException>(()=>new MeasuredTriangleScene(1,[new(default,default,default)]));
        Assert.Throws<ArgumentException>(()=>new XpbdCloth(Sheet().Definition,new(){Gravity=new(float.NaN,0,0)}));
        Assert.Throws<ArgumentException>(()=>new XpbdCloth(Sheet().Definition,new(){Iterations=17}));
        Assert.Throws<ArgumentException>(()=>new XpbdDefinition(new Vector3[513],new Vector2[513],[0,1,2],new float[513],[new(0,1,0)]));
        Vector3[] p=[new(-1,0,0),new(1,0,0),new(0,0,-1),new(0,0,1)];
        Assert.Throws<ArgumentException>(()=>new XpbdDefinition(p,new Vector2[4],[0,2,3,1,2,3],[1,1,1,1],[new(0,2,0)]));
    }
}
