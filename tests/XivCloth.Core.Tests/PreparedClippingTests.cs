using System.Numerics;
using XivCloth.Core;

namespace XivCloth.Core.Tests;

public sealed class PreparedClippingTests
{
    [Theory][InlineData(0f)][InlineData(9000f)]
    public void RemovingDuplicatePlaneDotsAndIdentityCopiesPreservesExactWitnessBits(float offset)
    {
        var random=new Random(812470);var covered=0;var empty=0;
        for(var sample=0;sample<12_000;sample++)
        {
            var rotation=Quaternion.CreateFromYawPitchRoll((float)random.NextDouble()*6,(float)random.NextDouble()*6,(float)random.NextDouble()*6);
            var shift=new Vector3(offset,-offset,offset);
            Vector3 T(Vector3 p)=>Vector3.Transform(p,rotation)+shift;
            var obstacle=new MeasuredTriangle(T(new(-1,0,-1)),T(new(0,0,1)),T(new(1,0,-1)));
            Vector3 Point()=>new((float)random.NextDouble()*4-2,(float)random.NextDouble()*.1f-.05f,(float)random.NextDouble()*4-2);
            var a=Point();var b=Point();var c=Point();
            if(sample%4==0){a=new(-1,.004f,-1);b=new(0,.004f,1);c=new(1,.004f,-1);}
            else if(sample%4==1){a=new(-.1f,-.02f,-.1f);b=new(0,-.02f,.1f);c=new(.1f,-.02f,-.1f);}
            var support=new PreparedTriangle(obstacle);var cloth=new MeasuredTriangle(T(a),T(b),T(c));
            var old=FrozenProjectedMinimum.Check(cloth,support,out var oldMinimum,out var oldBary);
            var current=TriangleContacts.ProjectedMinimum(cloth,support,out var minimum,out var bary);
            Assert.Equal(old,current);Assert.Equal(Bits(oldMinimum),Bits(minimum));
            Assert.Equal(Bits(oldBary.X),Bits(bary.X));Assert.Equal(Bits(oldBary.Y),Bits(bary.Y));Assert.Equal(Bits(oldBary.Z),Bits(bary.Z));
            if(current)covered++;else empty++;
        }
        Assert.True(covered>1000);Assert.True(empty>100);
    }
    private static long Bits(double value)=>BitConverter.DoubleToInt64Bits(value);
}
