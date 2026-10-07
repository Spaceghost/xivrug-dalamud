using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothRenderPoseTests
{
    [Fact]
    public void LiftIsAppliedOnceAndFlatFinalGeometryHasUnitUpNormals()
    {
        var mesh=Grid(2);var (positions,normals)=Prepare(mesh,lift:.5f,motion:false);
        for(var i=0;i<positions.Length;i++)
        {
            Assert.Equal(mesh.Positions[i]+Vector3.UnitY*.5f,positions[i]);
            Assert.Equal(Vector3.UnitY,normals[i]);
            // Exact idempotent vertex-shader safety operation, no second lift.
            Assert.Equal(positions[i].Y,Math.Max(mesh.GroundMinimum![i]+.001f,Math.Min(positions[i].Y,float.MaxValue)));
        }
    }

    [Fact]
    public void ContactCreatesNewSmoothNormalsFromActualSharedGeometryNotStaleInput()
    {
        var mesh=Grid(2);float[] ceilings=[.001f,.035f,.035f,.035f];
        var (positions,normals)=Prepare(mesh,ceilings,motion:false);
        Assert.NotEqual(mesh.Normals[0],normals[0]);
        var sums=new Vector3[4];
        for(var triangle=0;triangle<mesh.Indices.Length;triangle+=3)
        {
            var a=mesh.Indices[triangle];var b=mesh.Indices[triangle+1];var c=mesh.Indices[triangle+2];
            var face=Vector3.Cross(positions[b]-positions[a],positions[c]-positions[a]);if(face.Y<0)face=-face;
            sums[a]+=face;sums[b]+=face;sums[c]+=face;
        }
        for(var i=0;i<normals.Length;i++)Assert.Equal(Vector3.Normalize(sums[i]),normals[i]);
        // Expanded GPU triangle-list copies keep the SAME shared normal.
        foreach(var index in mesh.Indices)
            foreach(var same in mesh.Indices.Where(value=>value==index))Assert.Equal(normals[index],normals[same]);
        Assert.Equal(mesh.Positions.Select(p=>p.X),positions.Select(p=>p.X));
        Assert.Equal(mesh.Positions.Select(p=>p.Z),positions.Select(p=>p.Z));
    }

    [Theory]
    [InlineData(true,true,true,0f)] [InlineData(false,true,true,.15f)]
    [InlineData(true,false,true,.2f)] [InlineData(false,true,false,.1f)]
    public void FinalPositionsMatchPriorVertexEquationBeforeSmoothNormalReconstruction(bool circle,bool motion,bool edges,float lift)
    {
        var mesh=Grid(9);var ceilings=Enumerable.Repeat(float.MaxValue,mesh.Positions.Length).ToArray();
        for(var i=0;i<ceilings.Length;i+=3)ceilings[i]=.014f;
        var (positions,_)=Prepare(mesh,ceilings,circle,motion,edges,lift,1.23f);
        for(var i=0;i<positions.Length;i++)
        {
            var uv=mesh.UV[i];var x=(uv.X-.5f)*2;var z=(uv.Y-.5f)*2;
            var qx=Math.Abs(x)-1+.25f;var qz=Math.Abs(z)-1+.25f;
            var outline=circle ? MathF.Sqrt(x*x+z*z)-1
                : MathF.Sqrt(Math.Max(qx,0)*Math.Max(qx,0)+Math.Max(qz,0)*Math.Max(qz,0))+Math.Min(Math.Max(qx,qz),0)-.25f;
            var t=Math.Clamp((-outline-.04f)/.12f,0,1);var fringe=1-t*t*(3-2*t);
            var hem=.012f*fringe*(edges?1:0)*(motion?1:0)*(.5f+.5f*MathF.Sin(x*4+z*3-1.23f*2));
            var expected=Math.Max(.001f,Math.Min(mesh.Positions[i].Y+lift+hem,ceilings[i]));
            Assert.Equal(expected,positions[i].Y,6);
        }
    }

    [Theory]
    [InlineData(0f)] [InlineData(.1f)] [InlineData(8f)]
    public void LiftAndHemCannotReopenFullFootTriangleContact(float lift)
    {
        var mesh=Triangle([0,0,0]);
        ClothFootContact[] feet=[new(Vector2.Zero,.004f,.11f)];
        var ceilings=ClothContactConstraint.BuildRenderCeilings(mesh,feet,lift);
        var (positions,normals)=Prepare(mesh,ceilings,lift:lift);
        var expected=.001f+Math.Min(lift,.002f);
        Assert.All(positions,p=>Assert.Equal(expected,p.Y,6));
        Assert.All(normals,n=>Assert.Equal(Vector3.UnitY,n));
        var inside=positions[0]*(1-.10f/.133f-.01f/.133f)+positions[1]*(.10f/.133f)+positions[2]*(.01f/.133f);
        Assert.Equal(expected,inside.Y,6);
    }

    [Fact]
    public void SlopedFullContactRemainsThinInsteadOfAnInterpolatedSolePlateau()
    {
        var mesh=Triangle([0,-.2f,0]);ClothFootContact[] feet=[new(new(.10f,.01f),-.15f,.11f)];
        var ceilings=ClothContactConstraint.BuildRenderCeilings(mesh,feet,0);
        var (positions,normals)=Prepare(mesh,ceilings);
        for(var i=0;i<positions.Length;i++)Assert.Equal(mesh.GroundMinimum![i]+.001f,positions[i].Y,6);
        var at=positions[0]*.175f+positions[1]*.75f+positions[2]*.075f;
        Assert.Equal(-.149f,at.Y,6);
        Assert.All(normals,n=>Assert.InRange(Vector3.Dot(n,normals[0]),.99999f,1.00001f));
    }

    [Fact]
    public void EveryDrawUsesNewClockLiftAndAgeSweptContactWithoutMutatingSpringPublication()
    {
        var mesh=Grid(9);var before=mesh.Positions.ToArray();var oldNormals=mesh.Normals.ToArray();
        var (first,_)=Prepare(mesh,seconds:0);var (later,_)=Prepare(mesh,seconds:.8f);
        Assert.Contains(first.Zip(later),pair=>Math.Abs(pair.First.Y-pair.Second.Y)>.001f);
        ClothFootContact[] expanded=[new(Vector2.Zero,-.2f,1.1f)];
        var ceilings=ClothContactConstraint.BuildRenderCeilings(mesh,expanded,0);
        var (pinned,_)=Prepare(mesh,ceilings,seconds:.8f);
        Assert.Equal(.001f,pinned[pinned.Length/2].Y,6);
        Assert.Equal(before,mesh.Positions);Assert.Equal(oldNormals,mesh.Normals);
    }

    [Fact]
    public void CollapsedWallFacesAndOppositeWindingStayFinite()
    {
        var mesh=Triangle([0,0,0]) with {Positions=[Vector3.Zero,Vector3.Zero,Vector3.Zero],Indices=[2,1,0]};
        var (positions,normals)=Prepare(mesh);
        Assert.All(positions,p=>Assert.True(float.IsFinite(p.Y)));
        Assert.All(normals,n=>Assert.Equal(Vector3.UnitY,n));
    }

    [Fact]
    public void MalformedInputsAndAliasedBuffersFailClosed()
    {
        var mesh=Grid(2);var ceilings=Enumerable.Repeat(float.MaxValue,4).ToArray();var positions=new Vector3[4];var normals=new Vector3[4];
        Assert.Throws<ArgumentException>(()=>ClothRenderPose.Prepare(mesh,ceilings,Vector2.One,true,.25f,0,true,true,0,mesh.Positions,normals));
        Assert.Throws<ArgumentException>(()=>ClothRenderPose.Prepare(mesh,ceilings,Vector2.One,true,.25f,0,true,true,0,positions,positions));
        Assert.Throws<ArgumentException>(()=>ClothRenderPose.Prepare(mesh,ceilings,Vector2.One,true,.25f,float.NaN,true,true,0,positions,normals));
        var bad=mesh with {Indices=[0,1,99]};Assert.Throws<ArgumentException>(()=>Prepare(bad));
        ceilings[0]=float.NaN;Assert.Throws<ArgumentException>(()=>Prepare(mesh,ceilings));
    }

    [Fact]
    public void RetainedScratchHasZeroPerPreparationAllocation()
    {
        var mesh=Grid(34);var count=mesh.Positions.Length;var positions=new Vector3[count];var normals=new Vector3[count];
        var ceilings=Enumerable.Repeat(float.MaxValue,count).ToArray();
        ClothFootContact[] feet=[new(Vector2.Zero,.004f,.6f)];
        for(var i=0;i<20;i++)
        {
            ClothContactConstraint.BuildRenderCeilings(mesh,feet,.1f,ceilings);
            ClothRenderPose.Prepare(mesh,ceilings,Vector2.One,false,.25f,i*.01f,true,true,.1f,positions,normals);
        }
        var start=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<100;i++)
        {
            ClothContactConstraint.BuildRenderCeilings(mesh,feet,.1f,ceilings);
            ClothRenderPose.Prepare(mesh,ceilings,Vector2.One,false,.25f,i*.01f,true,true,.1f,positions,normals);
        }
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
    }

    [Fact]
    public void SpanCeilingsMatchArrayApiAndClearOldContactsWhenFeetDisappear()
    {
        var mesh=Grid(9);ClothFootContact[] feet=[new(Vector2.Zero,.004f,.6f)];
        var expected=ClothContactConstraint.BuildRenderCeilings(mesh,feet,.15f);
        var destination=new float[mesh.Positions.Length];
        ClothContactConstraint.BuildRenderCeilings(mesh,feet,.15f,destination);
        Assert.Equal(expected,destination);
        ClothContactConstraint.BuildRenderCeilings(mesh,[],.15f,destination);
        Assert.All(destination,value=>Assert.Equal(float.MaxValue,value));
        Assert.Throws<ArgumentException>(()=>ClothContactConstraint.BuildRenderCeilings(mesh,feet,.15f,new float[1]));
        Assert.Throws<ArgumentException>(()=>ClothContactConstraint.BuildRenderCeilings(mesh,feet,.15f,mesh.GroundMinimum!));
        Assert.All(mesh.GroundMinimum!,value=>Assert.Equal(0,value));
    }

    [Fact]
    public void AlternatingRollingWindowDimensionsReuseHighWaterScratchWithoutAllocating()
    {
        ClothMesh[] windows=[Grid(31),Grid(34),Grid(37)];
        var positions=new Vector3[37*37];var normals=new Vector3[37*37];var ceilings=new float[37*37];
        ClothFootContact[] feet=[new(Vector2.Zero,.004f,.6f)];
        for(var i=0;i<9;i++)Run(windows[i%3]);
        var start=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<90;i++)Run(windows[i%3]);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
        void Run(ClothMesh mesh)
        {
            var count=mesh.Positions.Length;
            ClothContactConstraint.BuildRenderCeilings(mesh,feet,.1f,ceilings.AsSpan(0,count));
            ClothRenderPose.Prepare(mesh,ceilings.AsSpan(0,count),Vector2.One,false,.25f,0,true,true,.1f,
                positions.AsSpan(0,count),normals.AsSpan(0,count));
        }
    }

    private static (Vector3[],Vector3[]) Prepare(ClothMesh mesh,float[]? ceilings=null,bool circle=true,bool motion=true,bool edges=true,float lift=0,float seconds=.2f)
    {
        ceilings??=Enumerable.Repeat(float.MaxValue,mesh.Positions.Length).ToArray();
        var positions=new Vector3[mesh.Positions.Length];var normals=new Vector3[mesh.Positions.Length];
        ClothRenderPose.Prepare(mesh,ceilings,Vector2.One,circle,.25f,seconds,motion,edges,lift,positions,normals);
        return(positions,normals);
    }
    private static ClothMesh Grid(int size)=>ClothSurface.Build(Vector2.Zero,Vector2.One,0,new float[size*size],size,0,false)
        with {GroundMinimum=new float[size*size]};
    private static ClothMesh Triangle(float[] ground)=>new(
        [new(0,ground[0]+.035f,0),new(.133f,ground[1]+.035f,0),new(0,ground[2]+.035f,.133f)],
        [Vector3.UnitY,Vector3.UnitY,Vector3.UnitY],[Vector2.Zero,Vector2.UnitX,Vector2.UnitY],[0,2,1],2,ground);
}
