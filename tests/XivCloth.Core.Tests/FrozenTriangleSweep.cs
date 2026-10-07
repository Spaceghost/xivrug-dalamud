using System.Numerics;
namespace XivCloth.Core;

internal static class FrozenTriangleSweep
{
    public const int MaximumDepth=12;
    private readonly record struct Slice(double Start,double End,int Depth);
    public static SweepVerdict Check(MeasuredTriangle before,MeasuredTriangle after,MeasuredTriangle obstacle,
        double clearance,ref CollisionWork work)
        =>Check(before,after,obstacle,clearance,ref work,out _);
    internal static SweepVerdict Check(MeasuredTriangle before,MeasuredTriangle after,MeasuredTriangle obstacle,
        double clearance,ref CollisionWork work,out double unresolvedTime)
    {
        unresolvedTime=double.NaN;
        if(!Valid(before)||!Valid(after)||!Valid(obstacle)||!double.IsFinite(clearance)||clearance is <=0 or >.1)
            return SweepVerdict.Invalid;
        Span<D3> first=stackalloc D3[3]{new(before.A),new(before.B),new(before.C)};
        Span<D3> last=stackalloc D3[3]{new(after.A),new(after.B),new(after.C)};
        Span<D3> fixedPoints=stackalloc D3[3]{new(obstacle.A),new(obstacle.B),new(obstacle.C)};
        Span<Slice> pending=stackalloc Slice[MaximumDepth+2];var count=1;pending[0]=new(0,1,0);
        Span<D3> hull=stackalloc D3[6];Span<R3> enclosure=stackalloc R3[6];
        while(count>0)
        {
            if(!work.Charge(1))return SweepVerdict.BudgetExceeded;
            var slice=pending[--count];
            for(var i=0;i<3;i++)
            {
                hull[i]=D3.Lerp(first[i],last[i],slice.Start);hull[i+3]=D3.Lerp(first[i],last[i],slice.End);
                enclosure[i]=R3.At(first[i],last[i],slice.Start);enclosure[i+3]=R3.At(first[i],last[i],slice.End);
            }
            if(Separated(hull,enclosure,fixedPoints,clearance))continue;
            if(slice.Depth==MaximumDepth)
            {unresolvedTime=(slice.Start+slice.End)*.5;return SweepVerdict.Unproven;}
            var midpoint=(slice.Start+slice.End)*.5;
            pending[count++]=new(midpoint,slice.End,slice.Depth+1);
            pending[count++]=new(slice.Start,midpoint,slice.Depth+1);
        }
        return SweepVerdict.Clear;
    }
    private static bool Valid(MeasuredTriangle t)=>Geometry.Valid(t.A)&&Geometry.Valid(t.B)&&Geometry.Valid(t.C)
        &&D3.Cross(new D3(t.B)-new D3(t.A),new D3(t.C)-new D3(t.A)).Squared>1e-20;
    private static bool Separated(ReadOnlySpan<D3> hull,ReadOnlySpan<R3> enclosed,ReadOnlySpan<D3> obstacle,double clearance)
    {
        // Axis-aligned/obstacle-plane witnesses make most floor queries cheap.
        if(Axis(new(1,0,0),enclosed,obstacle,clearance)||Axis(new(0,1,0),enclosed,obstacle,clearance)
            ||Axis(new(0,0,1),enclosed,obstacle,clearance)
            ||Axis(D3.Cross(obstacle[1]-obstacle[0],obstacle[2]-obstacle[0]),enclosed,obstacle,clearance))return true;
        // Coplanar polygons need in-plane edge normals: ordinary cross-edge
        // SAT axes all collapse to the shared plane normal in that case.
        var normal=D3.Cross(obstacle[1]-obstacle[0],obstacle[2]-obstacle[0]);
        for(var i=0;i<3;i++)
            if(Axis(D3.Cross(normal,obstacle[(i+1)%3]-obstacle[i]),enclosed,obstacle,clearance))return true;
        for(var i=0;i<6;i++)for(var j=i+1;j<6;j++)
            if(Axis(D3.Cross(normal,hull[j]-hull[i]),enclosed,obstacle,clearance))return true;
        for(var i=0;i<6;i++)for(var j=i+1;j<6;j++)for(var k=j+1;k<6;k++)
            if(Axis(D3.Cross(hull[j]-hull[i],hull[k]-hull[i]),enclosed,obstacle,clearance))return true;
        for(var i=0;i<6;i++)for(var j=i+1;j<6;j++)for(var k=0;k<3;k++)
            if(Axis(D3.Cross(hull[j]-hull[i],obstacle[(k+1)%3]-obstacle[k]),enclosed,obstacle,clearance))return true;
        return false;
    }
    private static bool Axis(D3 axis,ReadOnlySpan<R3> hull,ReadOnlySpan<D3> obstacle,double clearance)
    {
        if(!double.IsFinite(axis.Squared)||axis.Squared<1e-28)return false;
        var minimumA=double.PositiveInfinity;var maximumA=double.NegativeInfinity;
        var minimumB=double.PositiveInfinity;var maximumB=double.NegativeInfinity;
        foreach(var p in hull){var projection=p.Dot(axis);minimumA=Math.Min(minimumA,projection.Lo);maximumA=Math.Max(maximumA,projection.Hi);}
        foreach(var p in obstacle){var projection=new R3(p).Dot(axis);minimumB=Math.Min(minimumB,projection.Lo);maximumB=Math.Max(maximumB,projection.Hi);}
        var normSquared=Range.Point(axis.X)*axis.X+Range.Point(axis.Y)*axis.Y+Range.Point(axis.Z)*axis.Z;
        var margin=Math.BitIncrement(clearance*Math.BitIncrement(Math.Sqrt(normSquared.Hi)));
        return Math.BitDecrement(minimumA-maximumB)>margin||Math.BitDecrement(minimumB-maximumA)>margin;
    }
}
