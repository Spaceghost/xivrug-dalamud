using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class PreparedTriangleTests
{
    [Theory][InlineData(0f)][InlineData(9000f)]
    public void CachedPointCoordinatesMatchOriginalBoundaryPredicates(float offset)
    {
        var random=new Random(92408);var shift=new Vector3(offset,-offset,offset);
        for(var sample=0;sample<240;sample++)
        {
            var q=Quaternion.CreateFromYawPitchRoll((float)random.NextDouble()*6,(float)random.NextDouble()*6,(float)random.NextDouble()*6);
            Vector3 T(Vector3 p)=>Vector3.Transform(p,q)+shift;
            var triangle=new MeasuredTriangle(T(new(-1,0,-1)),T(new(0,0,1)),T(new(1,0,-1)));
            var prepared=new PreparedTriangle(triangle);
            for(var point=0;point<120;point++)
            {
                var u=(float)random.NextDouble()*1.2f-.1f;var v=(float)random.NextDouble()*1.2f-.1f;
                if(point%4==0)u=0;if(point%4==1)v=0;if(point%4==2)v=1-u;
                var p=triangle.A+u*(triangle.B-triangle.A)+v*(triangle.C-triangle.A);
                Assert.Equal(Geometry.InTriangle(p,triangle),prepared.ContainsProjected(p));
                // Probe immediate representable neighbors at every edge/point.
                p.X=MathF.BitIncrement(p.X);
                Assert.Equal(Geometry.InTriangle(p,triangle),prepared.ContainsProjected(p));
            }
        }
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void SkinnyDominantAxisAndPreparedNormalsRetainExactOriginalExpressions(int axis)
    {
        Vector3 Rotate(Vector3 p)=>axis==0?p:axis==1?new(p.Y,p.Z,p.X):new(p.Z,p.X,p.Y);
        var triangle=new MeasuredTriangle(Rotate(new(0,0,0)),Rotate(new(1,0,0)),Rotate(new(1,0,.00001f)));
        var prepared=new PreparedTriangle(triangle);D3[] fixedPoints=[new(triangle.A),new(triangle.B),new(triangle.C)];
        var normal=D3.Cross(fixedPoints[1]-fixedPoints[0],fixedPoints[2]-fixedPoints[0]);normal/=Math.Sqrt(normal.Squared);
        Assert.Equal(normal,prepared.Normal);
        for(var edge=0;edge<3;edge++)
        {
            var inward=D3.Cross(normal,fixedPoints[(edge+1)%3]-fixedPoints[edge]);inward/=Math.Sqrt(inward.Squared);
            Assert.Equal(inward,prepared.Inward(edge));
        }
        foreach(var p in new[]{Rotate(new(.75f,0,.000003f)),Rotate(new(.2f,0,.000004f)),triangle.A,triangle.B,triangle.C})
            Assert.Equal(Geometry.InTriangle(p,triangle),prepared.ContainsProjected(p));
    }
}
