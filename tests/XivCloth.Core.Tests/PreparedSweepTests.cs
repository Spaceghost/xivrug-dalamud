using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class PreparedSweepTests
{
    [Theory][InlineData(0f)][InlineData(9000f)]
    public void CachedFixedAxesPreserveFrozenVerdictWorkAndFirstUnresolvedSlice(float offset)
    {
        var random=new Random(615043);
        for(var sample=0;sample<480;sample++)
        {
            var rotation=Quaternion.CreateFromYawPitchRoll((float)random.NextDouble()*6,(float)random.NextDouble()*6,(float)random.NextDouble()*6);
            var shift=new Vector3(offset,-offset,offset);
            Vector3 T(Vector3 v)=>Vector3.Transform(v,rotation)+shift;
            var obstacle=new MeasuredTriangle(T(new(-1,0,-1)),T(new(0,0,1)),T(new(1,0,-1)));
            var p=new Vector3((float)random.NextDouble()*4-2,(float)random.NextDouble()*.05f-.025f,(float)random.NextDouble()*4-2);
            if(sample%4==0)p.Y=sample%8==0?MathF.BitIncrement(.002f):MathF.BitDecrement(.002f);
            var move=new Vector3((float)random.NextDouble()*.05f-.025f,(float)random.NextDouble()*.05f-.025f,(float)random.NextDouble()*.05f-.025f);
            var before=new MeasuredTriangle(T(p),T(p+new Vector3(.2f,0,.3f)),T(p+new Vector3(.3f,0,0)));
            var after=new MeasuredTriangle(T(p+move),T(p+move+new Vector3(.2f,.002f,.3f)),T(p+move+new Vector3(.3f,-.003f,0)));
            Compare(before,after,obstacle,.002,sample%5==0?3:10_000);
        }
    }
    [Fact]
    public void SkinnyAndGrazingFacesRemainUnprovenAtTheSameSlice()
    {
        foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
        {
            var rotation=Quaternion.CreateFromAxisAngle(axis,.61f);
            Vector3 T(Vector3 p)=>Vector3.Transform(p,rotation);
            var obstacle=new MeasuredTriangle(T(new(0,0,0)),T(new(1,0,.0001f)),T(new(1,0,0)));
            var before=new MeasuredTriangle(T(new(.4f,.005f,0)),T(new(.6f,.005f,.00004f)),T(new(.6f,.005f,0)));
            var after=new MeasuredTriangle(before.A-T(new(0,.01f,0)),before.B-T(new(0,.01f,0)),before.C-T(new(0,.01f,0)));
            Compare(before,after,obstacle,.002,10_000);
        }
    }
    [Fact]
    public void InvalidInputsAndExhaustedBudgetsNeverBecomeClear()
    {
        var good=new MeasuredTriangle(new(-1,0,-1),new(0,0,1),new(1,0,-1));
        foreach(var obstacle in new[]{good,default,new MeasuredTriangle(new(float.NaN,0,0),good.B,good.C)})
            foreach(var clearance in new[]{.002,0,double.NaN,.2})
                Compare(good,good,obstacle,clearance,0);
    }
    private static void Compare(MeasuredTriangle before,MeasuredTriangle after,MeasuredTriangle obstacle,double clearance,int budget)
    {
        var oldWork=new CollisionWork(budget);var newWork=new CollisionWork(budget);
        var old=FrozenTriangleSweep.Check(before,after,obstacle,clearance,ref oldWork,out var oldTime);
        var current=TriangleSweep.Check(before,after,new PreparedTriangle(obstacle),clearance,ref newWork,out var newTime);
        Assert.Equal(old,current);Assert.Equal(oldWork.Used,newWork.Used);Assert.Equal(oldWork.SweepNodes,newWork.SweepNodes);
        Assert.Equal(BitConverter.DoubleToInt64Bits(oldTime),BitConverter.DoubleToInt64Bits(newTime));
    }
}
