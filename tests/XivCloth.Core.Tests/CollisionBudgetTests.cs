using System.Diagnostics;
using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class CollisionBudgetTests(ITestOutputHelper output)
{
    private static XpbdDefinition Sheet(int size,float y)
    {
        var p=new Vector3[size*size];var uv=new Vector2[p.Length];var mass=Enumerable.Repeat(1f,p.Length).ToArray();
        var faces=new List<int>();var edges=new List<DistanceEdge>();
        for(var z=0;z<size;z++)for(var x=0;x<size;x++)
        {
            var i=z*size+x;p[i]=new((x-(size-1)*.5f)*.08f,y,(z-(size-1)*.5f)*.08f);
            if(x<size-1)edges.Add(new(i,i+1,0));if(z<size-1)edges.Add(new(i,i+size,0));
            if(x<size-1&&z<size-1)
            {faces.AddRange([i,i+size,i+1,i+1,i+size,i+size+1]);edges.Add(new(i,i+size+1,MaterialEdge.Shear));edges.Add(new(i+1,i+size,MaterialEdge.Shear));}
        }
        return new(p,uv,faces.ToArray(),mass,edges.ToArray());
    }
    private static MeasuredTriangleScene Floor()
    {
        var faces=new List<MeasuredTriangle>();
        for(var z=0;z<8;z++)for(var x=0;x<8;x++)
        {
            var a=new Vector3(x*.5f-2,0,z*.5f-2);var b=a+new Vector3(.5f,0,0);var c=a+new Vector3(0,0,.5f);var d=a+new Vector3(.5f,0,.5f);
            faces.Add(new(a,c,b));faces.Add(new(b,c,d));
        }
        return new(1,faces.ToArray());
    }
    [Theory]
    [InlineData(1f)][InlineData(.004f)]
    public void SmallLocalSheetCountersAndTimingAreReportedWithoutAnUnfoundedRealtimeAssertion(float y)
    {
        var solver=new XpbdCloth(Sheet(9,y),new(){Gravity=y>.1f?default:new(0,-9.81f,0)});var scene=Floor();
        for(var i=0;i<8;i++)Assert.Equal(XpbdStatus.Ready,solver.Advance(1d/60,scene).Status);
        var watch=Stopwatch.StartNew();XpbdAdvance result=default;var before=GC.GetAllocatedBytesForCurrentThread();
        var ready=true;
        for(var i=0;i<30;i++){result=solver.Advance(1d/60,scene);ready&=result.Status==XpbdStatus.Ready;}
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;watch.Stop();
        Assert.True(ready);
        output.WriteLine($"81 particles / 128 cloth faces / 128 scene faces, y={y:R}: {watch.Elapsed.TotalMilliseconds/30:F3}ms per60Hz Advance; pairs={result.TrianglePairs}; sweepNodes={result.SweepNodes}; treeNodes={result.BroadphaseNodes}; allocated={allocated}");
        Assert.True(result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes<=100_000);
        Assert.Equal(0,allocated);
    }
    [Fact]
    public void LargerOneSidedSoupStillRefusesAnInsufficientConfiguredBudgetTransactionally()
    {
        const int limit=1000;
        var solver=new XpbdCloth(Sheet(15,1),new(){Gravity=default,CollisionWorkLimit=limit});var initial=solver.Capture().Positions.ToArray();
        var result=solver.Advance(1d/60,Floor());
        Assert.Equal(XpbdStatus.WorkBudgetExceeded,result.Status);Assert.Equal(0,result.Substeps);
        Assert.Equal(limit,result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes);
        Assert.Equal(initial,solver.Capture().Positions.ToArray());Assert.Equal(0,solver.SceneGeneration);
        output.WriteLine($"225 particles / 392 cloth faces /128 scene faces: refused at {result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes} collision work units, state unchanged.");
    }

    [Fact]
    public void LargerEntirelyFreeSideSoupNowFitsUnchangedDefaultBudget()
    {
        var solver=new XpbdCloth(Sheet(15,1),new(){Gravity=default});
        var initial=solver.Capture().Positions.ToArray();var result=solver.Advance(1d/60,Floor());
        Assert.Equal(XpbdStatus.Ready,result.Status);Assert.Equal(2,result.Substeps);
        Assert.InRange(result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes,1,99_999);
        Assert.Equal(initial,solver.Capture().Positions.ToArray());Assert.Equal(1,solver.SceneGeneration);
        output.WriteLine($"225 particles entirely1unit above128 finite floors: accepted at {result.TrianglePairs+result.SweepNodes+result.BroadphaseNodes} work units without increasing default100000; no collision-heavy realtime claim.");
    }
}
