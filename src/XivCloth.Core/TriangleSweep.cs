using System.Numerics;

namespace XivCloth.Core;

public enum SweepVerdict { Clear, Unproven, BudgetExceeded, Invalid }

/// <summary>A hard count bound, not a wall-clock promise. All three query stages
/// charge the same budget before work; no exhausted query reports Clear.</summary>
public struct CollisionWork
{
    public const int Maximum=500_000;
    public int Limit { get; }
    public int Used { get; private set; }
    public int Pairs { get; private set; }
    public int SweepNodes { get; private set; }
    public int TreeNodes { get; private set; }
    public CollisionWork(int limit)
    {if(limit is <0 or >Maximum)throw new ArgumentOutOfRangeException(nameof(limit));Limit=limit;}
    internal bool Charge(int kind)
    {
        if(Used>=Limit)return false;Used++;
        if(kind==0)Pairs++;else if(kind==1)SweepNodes++;else TreeNodes++;
        return true;
    }
}

internal readonly record struct D3(double X,double Y,double Z)
{
    public D3(Vector3 p):this(p.X,p.Y,p.Z){}
    public static D3 operator +(D3 a,D3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static D3 operator -(D3 a,D3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static D3 operator -(D3 a)=>new(-a.X,-a.Y,-a.Z);
    public static D3 operator *(D3 a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
    public static D3 operator /(D3 a,double s)=>a*(1/s);
    public double Squared=>Dot(this,this);
    public Vector3 Float=>new((float)X,(float)Y,(float)Z);
    public static double Dot(D3 a,D3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public static D3 Cross(D3 a,D3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    public static D3 Lerp(D3 a,D3 b,double t)=>a+(b-a)*t;
}

// Outward-rounded support intervals. Candidate axes may be approximate: ANY
// axis is a valid separation witness if interval support bounds prove its gap.
internal readonly record struct Range(double Lo,double Hi)
{
    public static Range Point(double x)=>new(x,x);
    public static Range operator +(Range a,Range b)=>new(Math.BitDecrement(a.Lo+b.Lo),Math.BitIncrement(a.Hi+b.Hi));
    public static Range operator -(Range a,Range b)=>new(Math.BitDecrement(a.Lo-b.Hi),Math.BitIncrement(a.Hi-b.Lo));
    public static Range operator *(Range a,double b)=>b>=0
        ?new(Math.BitDecrement(a.Lo*b),Math.BitIncrement(a.Hi*b))
        :new(Math.BitDecrement(a.Hi*b),Math.BitIncrement(a.Lo*b));
}
internal readonly record struct R3(Range X,Range Y,Range Z)
{
    public R3(D3 p):this(Range.Point(p.X),Range.Point(p.Y),Range.Point(p.Z)){}
    public static R3 At(D3 from,D3 to,double t)=>new(
        Range.Point(from.X)+(Range.Point(to.X)-Range.Point(from.X))*t,
        Range.Point(from.Y)+(Range.Point(to.Y)-Range.Point(from.Y))*t,
        Range.Point(from.Z)+(Range.Point(to.Z)-Range.Point(from.Z))*t);
    public Range Dot(D3 axis)=>X*axis.X+Y*axis.Y+Z*axis.Z;
}

/// <summary>Conservative continuous rejection for a linearly moving finite
/// triangle against a static finite triangle. Every accepted time interval has
/// a separating support-plane proof for the convex hull of its six endpoints.
/// Linear intermediate triangles are inside that hull. Interval arithmetic
/// rounds support bounds outward; finite depth/work uncertainty is never clear.
/// This is not exact time-of-impact calculation, nor a general curved-path CCD.</summary>
public static class TriangleSweep
{
    public const int MaximumDepth=12;
    private readonly record struct Slice(double Start,double End,int Depth);
    public static SweepVerdict Check(MeasuredTriangle before,MeasuredTriangle after,MeasuredTriangle obstacle,
        double clearance,ref CollisionWork work)
        =>Check(before,after,obstacle,clearance,ref work,out _);
    internal static SweepVerdict Check(MeasuredTriangle before,MeasuredTriangle after,MeasuredTriangle obstacle,
        double clearance,ref CollisionWork work,out double unresolvedTime)
        =>Check(before,after,new PreparedTriangle(obstacle),clearance,ref work,out unresolvedTime);
    internal static SweepVerdict Check(MeasuredTriangle before,MeasuredTriangle after,in PreparedTriangle prepared,
        double clearance,ref CollisionWork work,out double unresolvedTime)
    {
        var obstacle=prepared.Source;
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
            if(Separated(hull,enclosure,fixedPoints,prepared,clearance))continue;
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
    private static bool Separated(ReadOnlySpan<D3> hull,ReadOnlySpan<R3> enclosed,ReadOnlySpan<D3> obstacle,in PreparedTriangle prepared,double clearance)
    {
        // Axis-aligned/obstacle-plane witnesses make most floor queries cheap.
        if(prepared.SweepFixedSeparated(enclosed,clearance))return true;
        // Coplanar polygons need in-plane edge normals: ordinary cross-edge
        // SAT axes all collapse to the shared plane normal in that case.
        var normal=prepared.RawNormal;
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

internal readonly record struct Box3(Vector3 Minimum,Vector3 Maximum)
{
    public static Box3 Triangle(MeasuredTriangle t)=>new(Vector3.Min(t.A,Vector3.Min(t.B,t.C)),Vector3.Max(t.A,Vector3.Max(t.B,t.C)));
    public static Box3 Union(Box3 a,Box3 b)=>new(Vector3.Min(a.Minimum,b.Minimum),Vector3.Max(a.Maximum,b.Maximum));
    public Box3 Expand(float amount)=>new(new(MathF.BitDecrement(Minimum.X-amount),MathF.BitDecrement(Minimum.Y-amount),MathF.BitDecrement(Minimum.Z-amount)),
        new(MathF.BitIncrement(Maximum.X+amount),MathF.BitIncrement(Maximum.Y+amount),MathF.BitIncrement(Maximum.Z+amount)));
    public bool Intersects(Box3 other)=>Minimum.X<=other.Maximum.X&&Maximum.X>=other.Minimum.X
        &&Minimum.Y<=other.Maximum.Y&&Maximum.Y>=other.Minimum.Y&&Minimum.Z<=other.Maximum.Z&&Maximum.Z>=other.Minimum.Z;
}

/// <summary>Immutable tiny local BVH. Bounds enclose original float vertices;
/// conservative query expansion encloses the whole linear swept cloth face.</summary>
internal sealed class StaticTriangleTree
{
    private readonly record struct Node(Box3 Bounds,int Left,int Right,int Triangle);
    private readonly Node[] nodes;
    private readonly int root;
    public StaticTriangleTree(MeasuredTriangle[] triangles)
    {
        if(triangles.Length==0){nodes=[];root=-1;return;}
        var built=new List<Node>(triangles.Length*2);var ids=Enumerable.Range(0,triangles.Length).ToArray();
        var boxes=triangles.Select(Box3.Triangle).ToArray();root=Build(0,ids.Length);nodes=built.ToArray();
        int Build(int start,int count)
        {
            var box=boxes[ids[start]];for(var i=start+1;i<start+count;i++)box=Box3.Union(box,boxes[ids[i]]);
            var index=built.Count;built.Add(default);
            if(count==1){built[index]=new(box,-1,-1,ids[start]);return index;}
            var extent=box.Maximum-box.Minimum;var axis=extent.X>=extent.Y&&extent.X>=extent.Z?0:extent.Y>=extent.Z?1:2;
            Array.Sort(ids,start,count,Comparer<int>.Create((a,b)=>
            {
                var ca=boxes[a].Minimum+boxes[a].Maximum;var cb=boxes[b].Minimum+boxes[b].Maximum;
                var comparison=(axis==0?ca.X:axis==1?ca.Y:ca.Z).CompareTo(axis==0?cb.X:axis==1?cb.Y:cb.Z);
                return comparison!=0?comparison:a.CompareTo(b);
            }));
            var half=count/2;var left=Build(start,half);var right=Build(start+half,count-half);
            built[index]=new(box,left,right,-1);return index;
        }
    }
    public int Query(Box3 box,Span<int> candidates,ref CollisionWork work)
    {
        if(root<0)return 0;
        Span<int> stack=stackalloc int[MeasuredTriangleScene.MaximumTriangles*2];var count=1;var found=0;stack[0]=root;
        while(count>0)
        {
            if(!work.Charge(2))return -1;
            var node=nodes[stack[--count]];if(!box.Intersects(node.Bounds))continue;
            if(node.Triangle>=0)
            {
                if(found>=candidates.Length)return -1;
                candidates[found++]=node.Triangle;
            }
            else{stack[count++]=node.Left;stack[count++]=node.Right;}
        }
        candidates[..found].Sort();return found;
    }
}
