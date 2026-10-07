using System.Diagnostics;
using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class StairBudgetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true,0f)][InlineData(false,0f)]
    [InlineData(true,.61f)][InlineData(false,.61f)]
    public void SameStairsWithTwoUpperPinsContinueDrapingWithinActualSupport(bool circle,float yaw)
    {
        var pattern=circle?ClothRestPattern.Circle(2,9,height:.65f,pinnedVertices:[8,80])
            :ClothRestPattern.RoundedRectangle(2,1.6f,.35f,9,9,height:.65f,pinnedVertices:[8,80]);
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
        var initial=pattern.Positions.ToArray().Select(p=>Vector3.Transform(p,rotation)).ToArray();
        var solver=new XpbdCloth(pattern.ToDefinition());
        solver.Reset(initial);var scene=Stairs(yaw);var peak=0;
        for(var frame=0;frame<180;frame++)
        {
            var step=solver.Advance(1d/60,scene);
            Assert.True(step.Status==XpbdStatus.Ready,$"frame={frame} status={step.Status}");
            peak=Math.Max(peak,step.TrianglePairs+step.SweepNodes+step.BroadphaseNodes);
        }
        var final=solver.Capture().Positions.ToArray();
        var local=final.Select(p=>Vector3.Transform(p,Quaternion.Conjugate(rotation))).ToArray();
        var contacts=0;var minimum=local.Min(p=>p.Y);var maximum=local.Max(p=>p.Y);
        foreach(var p in local)
        {
            Assert.InRange(p.X,-2,2);Assert.InRange(p.Z,-1.5f,1.5f);
            var floor=p.X<-.5f?0:p.X<0?.15f:p.X<.5f?.3f:.45f;
            Assert.True(p.Y>=floor-.00001f,$"point={p} floor={floor}");
            if(p.Y-floor<.025f)contacts++;
        }
        Assert.Equal(initial[8],final[8]);Assert.Equal(initial[80],final[80]);
        Assert.True(minimum<.35f&&maximum-minimum>.25f&&contacts>=3,
            $"No continuing bend/descent with multiple floor contacts: min={minimum:R} max={maximum:R} contacts={contacts}");
        Assert.InRange(peak,0,100_000);
        output.WriteLine($"pinned {(circle?"circle":"rounded")} yaw={yaw:R}:180accepted, min={minimum:R} max={maximum:R}, contacts={contacts}, peakWork={peak}; unchanged default100k");
    }

    [Theory]
    [InlineData(true,0f,100_000)][InlineData(false,0f,100_000)]
    [InlineData(true,.61f,100_000)][InlineData(false,.61f,100_000)]
    [InlineData(true,0f,500_000)][InlineData(false,0f,500_000)]
    [InlineData(true,.61f,500_000)][InlineData(false,.61f,500_000)]
    public void ReportsActualPatternStairCadenceWithoutChangingCollisionBudget(bool circle,float yaw,int budget)
    {
        var pattern=circle?ClothRestPattern.Circle(2,9,height:.65f)
            :ClothRestPattern.RoundedRectangle(2,1.6f,.35f,9,9,height:.65f);
        var definition=pattern.ToDefinition();var solver=new XpbdCloth(definition,new(){CollisionWorkLimit=budget});
        if(yaw!=0)solver.Reset(pattern.Positions.ToArray().Select(p=>Vector3.Transform(p,Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw))).ToArray());
        var scene=Stairs(yaw);var samples=new double[180];var accepted=0;var peak=0;XpbdAdvance last=default;
        var initial=solver.Capture();solver.Advance(1d/60,scene);solver.Reset(initial.Positions);
        // Free cloth can slide onto the lower landing by the final frame. Require
        // descent and bending during the trajectory, not one CPU-dependent final pose.
        var peakBend=0f;
        var samplesPose=new Vector3[definition.VertexCount];
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(var frame=0;frame<samples.Length;frame++)
        {
            var start=Stopwatch.GetTimestamp();last=solver.Advance(1d/60,scene);
            samples[frame]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            peak=Math.Max(peak,last.TrianglePairs+last.SweepNodes+last.BroadphaseNodes);
            if(last.Status!=XpbdStatus.Ready)break;
            accepted++;
            var frameMin=float.PositiveInfinity;var frameMax=float.NegativeInfinity;
            solver.CopyPositionsTo(samplesPose);
            foreach(var point in samplesPose){frameMin=Math.Min(frameMin,point.Y);frameMax=Math.Max(frameMax,point.Y);}
            peakBend=Math.Max(peakBend,frameMax-frameMin);
        }
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        var final=solver.Capture().Positions.ToArray();
        var minimum=final.Min(p=>p.Y);var maximum=final.Max(p=>p.Y);var mean=final.Average(p=>p.Y);
        output.WriteLine($"budget={budget}, final height min={minimum:R} max={maximum:R} mean={mean:R}");
        var sorted=samples.Take(accepted).Order().ToArray();
        output.WriteLine($"shape={(circle?"circle":"rounded")}, yaw={yaw:R}, particles={definition.VertexCount}, clothFaces={pattern.Indices.Length/3}, sceneFaces={scene.Triangles.Length}; accepted={accepted}/180, final={last.Status}, peakWork={peak}, pairs={last.TrianglePairs}, tree={last.BroadphaseNodes}, intervals={last.SweepNodes}, allocated={allocated}; accepted wall mean={(accepted>0?sorted.Average():0):F4}ms max={(accepted>0?sorted[^1]:0):F4}ms");
        Assert.InRange(peak,0,budget);
        Assert.Equal(0,allocated);
        Assert.Equal(180,accepted);Assert.Equal(XpbdStatus.Ready,last.Status);
        Assert.True(mean<.45f&&peakBend>.1f,"The material must descend and bend during its trajectory, not remain frozen above the stairs.");
    }

    internal static MeasuredTriangleScene Stairs(float yaw)
    {
        var triangles=new List<MeasuredTriangle>(128);var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
        Vector3 T(Vector3 p)=>Vector3.Transform(p,rotation);
        void Face(Vector3 a,Vector3 b,Vector3 c)=>triangles.Add(new(T(a),T(b),T(c)));
        for(var step=0;step<4;step++)for(var strip=0;strip<8;strip++)
        {
            var x=-1+step*.5f;var z=-1.5f+strip*.375f;var y=step*.15f;
            var endX=step==3?2:x+.5f;
            var a=new Vector3(x,y,z);var b=new Vector3(x,y,z+.375f);
            var c=new Vector3(endX,y,z);var d=new Vector3(endX,y,z+.375f);
            Face(a,b,c);Face(c,b,d);
            if(step<3)
            {
                var e=c+new Vector3(0,.15f,0);var f=d+new Vector3(0,.15f,0);
                Face(c,d,e);Face(e,d,f);
            }
            // Lower landing plus the extended last tread avoid an invented
            // vertical stop exactly at the circular cloth's perimeter.
            if(step==0)
            {
                var e=new Vector3(-2,0,z);var f=new Vector3(-2,0,z+.375f);
                Face(e,f,a);Face(a,f,b);
            }
        }
        return new(1,triangles.ToArray());
    }
}
