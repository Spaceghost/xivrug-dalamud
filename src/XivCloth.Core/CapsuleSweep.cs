using System.Numerics;

namespace XivCloth.Core;

/// <summary>Conservative triangle vs linearly moving capsule proof. Transform
/// into the moving first endpoint's frame: its translation is removed exactly
/// in double from original floats. The remaining segment and cloth triangle
/// have linear endpoints. Interval support gaps must exceed radius+clearance;
/// finite subdivision/budget uncertainty is never Clear. No curved-path claim.</summary>
public static class CapsuleSweep
{
    private readonly record struct Slice(double Start,double End,int Depth);
    public static SweepVerdict Check(MeasuredTriangle before,MeasuredTriangle after,FootCapsule bodyBefore,FootCapsule bodyAfter,
        double clearance,ref CollisionWork work)
    {
        if(!Valid(before)||!Valid(after)||!bodyBefore.Valid||!bodyAfter.Valid||!double.IsFinite(clearance)||clearance is <=0 or >.1)
            return SweepVerdict.Invalid;
        var origin=new D3(bodyBefore.A);var endOrigin=new D3(bodyAfter.A);
        Span<D3> first=stackalloc D3[3]{new D3(before.A)-origin,new D3(before.B)-origin,new D3(before.C)-origin};
        Span<D3> last=stackalloc D3[3]{new D3(after.A)-endOrigin,new D3(after.B)-endOrigin,new D3(after.C)-endOrigin};
        var firstAxis=new D3(bodyBefore.B)-origin;var lastAxis=new D3(bodyAfter.B)-endOrigin;
        var radius=Math.BitIncrement(Math.Max(bodyBefore.Radius,bodyAfter.Radius)+clearance);
        Span<Slice> stack=stackalloc Slice[TriangleSweep.MaximumDepth+2];var count=1;stack[0]=new(0,1,0);
        Span<D3> points=stackalloc D3[6];Span<R3> enclosed=stackalloc R3[6];
        Span<D3> axisPoints=stackalloc D3[3];Span<R3> axisEnclosed=stackalloc R3[3];
        axisPoints[0]=default;axisEnclosed[0]=new(new D3());
        while(count>0)
        {
            if(!work.Charge(1))return SweepVerdict.BudgetExceeded;
            var slice=stack[--count];
            for(var i=0;i<3;i++)
            {
                points[i]=D3.Lerp(first[i],last[i],slice.Start);points[i+3]=D3.Lerp(first[i],last[i],slice.End);
                enclosed[i]=R3.At(first[i],last[i],slice.Start);enclosed[i+3]=R3.At(first[i],last[i],slice.End);
            }
            axisPoints[1]=D3.Lerp(firstAxis,lastAxis,slice.Start);axisPoints[2]=D3.Lerp(firstAxis,lastAxis,slice.End);
            axisEnclosed[1]=R3.At(firstAxis,lastAxis,slice.Start);axisEnclosed[2]=R3.At(firstAxis,lastAxis,slice.End);
            if(Separated(points,enclosed,axisPoints,axisEnclosed,radius))continue;
            if(slice.Depth==TriangleSweep.MaximumDepth)return SweepVerdict.Unproven;
            var mid=(slice.Start+slice.End)*.5;
            stack[count++]=new(mid,slice.End,slice.Depth+1);stack[count++]=new(slice.Start,mid,slice.Depth+1);
        }
        return SweepVerdict.Clear;
    }
    private static bool Valid(MeasuredTriangle t)=>Geometry.Valid(t.A)&&Geometry.Valid(t.B)&&Geometry.Valid(t.C)
        &&D3.Cross(new D3(t.B)-new D3(t.A),new D3(t.C)-new D3(t.A)).Squared>1e-20;
    private static bool Separated(ReadOnlySpan<D3> points,ReadOnlySpan<R3> enclosed,ReadOnlySpan<D3> body,ReadOnlySpan<R3> bodyEnclosed,double radius)
    {
        if(Axis(new(1,0,0),enclosed,bodyEnclosed,radius)||Axis(new(0,1,0),enclosed,bodyEnclosed,radius)
            ||Axis(new(0,0,1),enclosed,bodyEnclosed,radius))return true;
        // Closest-feature directions find curved capsule separation witnesses;
        // approximate witnesses NEVER replace the interval support proof.
        var first=new MeasuredTriangle(points[0].Float,points[1].Float,points[2].Float);
        var last=new MeasuredTriangle(points[3].Float,points[4].Float,points[5].Float);
        var a=TriangleContacts.Closest(first,new(default,body[1].Float,body[1].Float));
        var b=TriangleContacts.Closest(last,new(default,body[2].Float,body[2].Float));
        if(Axis(a.Cloth-a.Obstacle,enclosed,bodyEnclosed,radius)||Axis(b.Cloth-b.Obstacle,enclosed,bodyEnclosed,radius))return true;
        for(var i=0;i<6;i++)for(var j=0;j<3;j++)if(Axis(points[i]-body[j],enclosed,bodyEnclosed,radius))return true;
        for(var i=0;i<6;i++)for(var j=i+1;j<6;j++)for(var k=j+1;k<6;k++)
            if(Axis(D3.Cross(points[j]-points[i],points[k]-points[i]),enclosed,bodyEnclosed,radius))return true;
        for(var i=0;i<6;i++)for(var j=i+1;j<6;j++)for(var k=1;k<3;k++)
            if(Axis(D3.Cross(points[j]-points[i],body[k]),enclosed,bodyEnclosed,radius))return true;
        return false;
    }
    private static bool Axis(D3 axis,ReadOnlySpan<R3> cloth,ReadOnlySpan<R3> body,double radius)
    {
        if(!double.IsFinite(axis.Squared)||axis.Squared<1e-28)return false;
        var minA=double.PositiveInfinity;var maxA=double.NegativeInfinity;var minB=double.PositiveInfinity;var maxB=double.NegativeInfinity;
        foreach(var p in cloth){var v=p.Dot(axis);minA=Math.Min(minA,v.Lo);maxA=Math.Max(maxA,v.Hi);}
        foreach(var p in body){var v=p.Dot(axis);minB=Math.Min(minB,v.Lo);maxB=Math.Max(maxB,v.Hi);}
        var squared=Range.Point(axis.X)*axis.X+Range.Point(axis.Y)*axis.Y+Range.Point(axis.Z)*axis.Z;
        var margin=Math.BitIncrement(radius*Math.BitIncrement(Math.Sqrt(squared.Hi)));
        return Math.BitDecrement(minA-maxB)>margin||Math.BitDecrement(minB-maxA)>margin;
    }
}
