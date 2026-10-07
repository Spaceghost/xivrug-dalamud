using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class ResponseSeparationTests
{
    private static MeasuredTriangle Floor(float y=0)=>new(new(-1,y,-1),new(0,y,1),new(1,y,-1));
    [Theory][InlineData(0f)][InlineData(9000f)]
    public void ProvedFiniteSeparationNeverOmitsALegacyResponseWitness(float offset)
    {
        var random=new Random(48152);var shift=new Vector3(offset,-offset,offset);const float thickness=.004f;
        var cutoff=Math.Max(thickness,Math.BitIncrement(Math.Sqrt(thickness*thickness)))+1e-9;
        var pruned=0;
        for(var sample=0;sample<2400;sample++)
        {
            var q=Quaternion.CreateFromYawPitchRoll((float)random.NextDouble()*6,(float)random.NextDouble()*6,(float)random.NextDouble()*6);
            Vector3 T(Vector3 p)=>Vector3.Transform(p,q)+shift;
            var obstacle=new MeasuredTriangle(T(new(-1,0,-1)),T(new(0,0,1)),T(new(1,0,-1)));
            var origin=new Vector3((float)random.NextDouble()*3-1.5f,(float)random.NextDouble()*.1f-.05f,(float)random.NextDouble()*3-1.5f);
            if(sample%3==0)origin.Y=sample%2==0?MathF.BitDecrement(thickness):MathF.BitIncrement(thickness);
            var face=new MeasuredTriangle(T(origin),T(origin+new Vector3(.02f,0,.03f)),T(origin+new Vector3(.03f,0,0)));
            var prepared=new PreparedTriangle(obstacle);
            if(!prepared.SeparatesFinite(face,cutoff))continue;
            pruned++;
            Assert.True(TriangleContacts.Closest(face,obstacle).Squared>=thickness*thickness);
        }
        Assert.True(pruned>300);
    }
    [Fact]
    public void TouchingClearanceAndGrazingFiniteEdgesStayUnpruned()
    {
        const float thickness=.004f;var floor=Floor();var prepared=new PreparedTriangle(floor);
        var at=Floor(thickness);
        Assert.False(prepared.SeparatesFinite(at,Math.BitIncrement(Math.Sqrt(thickness*thickness))+1e-9));
        Assert.False(prepared.SeparatesFinite(Floor(),thickness));
        Assert.True(prepared.SeparatesFinite(Floor(.02f),thickness));
        Assert.True(prepared.SeparatesFinite(Floor(-.02f),thickness));
        Assert.False(prepared.SeparatesFinite(at,double.NaN));
        Assert.False(new PreparedSeparationAxis(default,new(floor.A),new(floor.B),new(floor.C)).ProvesSeparated(at,thickness));
    }
    [Fact]
    public void FiniteSeparationDoesNotBypassOneSidedResponseOrDeepAdmission()
    {
        var face=new MeasuredTriangle(new(-.1f,-.02f,-.1f),new(0,-.02f,.1f),new(.1f,-.02f,-.1f));
        var floor=Floor();Assert.True(new PreparedTriangle(floor).SeparatesFinite(face,.004));
        Vector3[] points=[face.A,face.B,face.C];var before=(Vector3[])points.Clone();var work=new CollisionWork(10000);
        var scene=new MeasuredTriangleScene(1,[floor]);
        for(var i=0;i<3;i++)Assert.True(TriangleContacts.Project(points,before,[0,1,2],[1,1,1],scene,.004f,ref work));
        Assert.All(points,p=>Assert.True(p.Y>=.00399f));
        for(var i=0;i<points.Length;i++)points[i]=before[i]-new Vector3(0,.1f,0);
        Assert.Equal(SweepVerdict.Unproven,TriangleContacts.OneSidedClear(points,[0,1,2],scene,.002f,ref work));
    }
}
