using System.Numerics;

namespace XivSurface.Core;

/// <summary>
/// Bounded geometric union proof, not an area sum or point-sample heuristic.
/// Subtracts actual coplanar collision faces from the convex hull of a
/// compressed material cell. Every remaining positive-area fragment must be
/// covered. The caller owns local-layer selection and evidence freshness.
/// Reusable scratch storage is single-threaded like the floor sampler.
/// </summary>
public sealed class CoplanarCellCoverage
{
    public const int MaximumTriangles = 256;
    public const int MaximumPieces = 128;
    public const int MaximumVertices = 32;
    public const int MaximumOperations = 65_536;
    private readonly record struct Point(double X, double Z)
    {
        public static Point operator -(Point a, Point b) => new(a.X-b.X,a.Z-b.Z);
    }
    private Point[] current = new Point[MaximumPieces * MaximumVertices];
    private Point[] next = new Point[MaximumPieces * MaximumVertices];
    private int[] counts = new int[MaximumPieces], nextCounts = new int[MaximumPieces];
    private readonly Point[] carry = new Point[MaximumVertices], inside = new Point[MaximumVertices], outside = new Point[MaximumVertices];
    private Func<bool>? deadline;
    private Vector2 origin;
    public int LastOperations { get; private set; }
    public int LastPeakPieces { get; private set; }
    public Vector2? MissingWitness { get; private set; }
    public double MissingArea { get; private set; }

    public bool Proves(LayerFloorHit measuredCenter, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        IReadOnlyList<LayerTriangle> triangles, Func<bool>? withinDeadline = null)
    {
        LastOperations = LastPeakPieces = 0; MissingWitness=null; MissingArea=0; deadline = withinDeadline;
        if (triangles.Count is < 1 or > MaximumTriangles || !measuredCenter.Valid || deadline?.Invoke() == false) return false;
        var midpoint = (a+b+c+d)/4;
        if (!MathEx.Finite(midpoint) || Vector2.DistanceSquared(new(midpoint.X,midpoint.Z),new(measuredCenter.Position.X,measuredCenter.Position.Z))
            > LayerFloorHit.PlanarHeightTolerance*LayerFloorHit.PlanarHeightTolerance) return false;
        origin = new(a.X,a.Z);
        Span<Vector3> corners = stackalloc Vector3[] { a,b,d,c };
        foreach (var corner in corners)
            if (!OnPlane(measuredCenter.Triangle,corner)) return false;
        // Folded material cells retain their original topology and UVs. A
        // fully measured convex hull is a conservative coverage superset of
        // both checkerboard diagonals, including reversed or concave cells.
        Span<Vector3> hull=stackalloc Vector3[4];
        var hullCount=ClothContactFootprint.Hull(a,b,c,d,hull);
        if(hullCount<3)return false;
        for(var i=0;i<triangles.Count;i++)
        {
            var triangle=triangles[i];
            if (!Spend() || deadline?.Invoke()==false || !triangle.Walkable || !OnPlane(measuredCenter.Triangle,triangle.A)
                || !OnPlane(measuredCenter.Triangle,triangle.B) || !OnPlane(measuredCenter.Triangle,triangle.C)) return false;
        }
        return Covered(hull[0],hull[1],hull[2],triangles)
            && (hullCount==3 || Covered(hull[0],hull[2],hull[3],triangles)) && deadline?.Invoke() != false;
    }

    public static bool OnPlane(LayerTriangle plane, Vector3 point) => MathEx.Finite(point)
        && plane.TryHeight(new(point.X,point.Z),out var y) && Math.Abs((double)point.Y-y) <= LayerFloorHit.PlanarHeightTolerance;

    private bool Covered(Vector3 a,Vector3 b,Vector3 c,IReadOnlyList<LayerTriangle> triangles)
    {
        current[0]=Local(a); current[1]=Local(b); current[2]=Local(c); counts[0]=3;
        var pieces=1; LastPeakPieces=Math.Max(LastPeakPieces,1);
        Span<Point> edges=stackalloc Point[3];
        for(var triangleIndex=0;triangleIndex<triangles.Count;triangleIndex++)
        {
            var triangle=triangles[triangleIndex];
            if (!Spend() || deadline?.Invoke() == false) return false;
            var ta=Local(triangle.A); var tb=Local(triangle.B); var tc=Local(triangle.C);
            var orientation=Math.Sign(Cross(tb-ta,tc-ta));
            if (orientation==0) return false;
            edges[0]=ta; edges[1]=tb; edges[2]=tc;
            var nextPieces=0;
            for (var piece=0;piece<pieces;piece++)
            {
                var count=counts[piece]; current.AsSpan(piece*MaximumVertices,count).CopyTo(carry);
                // A distant face must not subdivide the uncovered polygon
                // along its infinite edge lines. Keep disjoint pieces whole.
                var disjoint=false;
                for(var edge=0;edge<3 && !disjoint;edge++)
                {
                    var start=edges[edge];var delta=edges[(edge+1)%3]-start;
                    var outsideAll=true;
                    for(var vertex=0;vertex<count;vertex++)
                    {
                        if(!Spend())return false;
                        if(orientation*Cross(delta,carry[vertex]-start)>0){outsideAll=false;break;}
                    }
                    disjoint=outsideAll;
                }
                if(disjoint)
                {
                    if(nextPieces>=MaximumPieces)return false;
                    carry.AsSpan(0,count).CopyTo(next.AsSpan(nextPieces*MaximumVertices));
                    nextCounts[nextPieces++]=count;continue;
                }
                // Triangle complement is partitioned into at most three
                // convex outside fragments. Only the final inside fragment
                // is removed. Repeated/overlapping source faces cannot erase
                // an uncovered hole by counting the same area twice.
                for (var edge=0;edge<3 && count>0;edge++)
                {
                    if (!Split(count,edges[edge],edges[(edge+1)%3],orientation,out var insideCount,out var outsideCount)) return false;
                    if (HasArea(outside.AsSpan(0,outsideCount)))
                    {
                        if (nextPieces>=MaximumPieces) return false;
                        outside.AsSpan(0,outsideCount).CopyTo(next.AsSpan(nextPieces*MaximumVertices));
                        nextCounts[nextPieces++]=outsideCount;
                    }
                    inside.AsSpan(0,insideCount).CopyTo(carry); count=insideCount;
                }
            }
            if (nextPieces==0) return true;
            LastPeakPieces=Math.Max(LastPeakPieces,nextPieces);
            (current,next)=(next,current); (counts,nextCounts)=(nextCounts,counts); pieces=nextPieces;
        }
        // A real uncovered convex fragment supplies a discovery target. Use
        // its interior average rather than an arbitrary bounding-box point.
        // Never publish a witness on an operation/deadline failure above.
        var uncovered=false;
        for(var piece=0;piece<pieces;piece++)
        {
            var polygon=current.AsSpan(piece*MaximumVertices,counts[piece]);
            if(!HasArea(polygon))continue;
            uncovered=true;
            double x=0,z=0,area=0;
            foreach(var point in polygon){x+=point.X;z+=point.Z;}
            for(var i=1;i+1<polygon.Length;i++)area+=Cross(polygon[i]-polygon[0],polygon[i+1]-polygon[0]);
            var size=Math.Abs(area)*.5;
            if(size<=MissingArea)continue;
            MissingArea=size;
            MissingWitness=new((float)(origin.X+x/polygon.Length),(float)(origin.Y+z/polygon.Length));
        }
        return !uncovered;
    }

    private bool Split(int count,Point start,Point end,int orientation,out int insideCount,out int outsideCount)
    {
        insideCount=outsideCount=0;
        var edge=end-start;
        for(var i=0;i<count;i++)
        {
            if (!Spend()) return false;
            var a=carry[i]; var b=carry[(i+1)%count];
            var sa=orientation*Cross(edge,a-start); var sb=orientation*Cross(edge,b-start);
            if (sa>=0 && !Append(inside,ref insideCount,a)) return false;
            if (sa<=0 && !Append(outside,ref outsideCount,a)) return false;
            if ((sa<0 && sb>0) || (sa>0 && sb<0))
            {
                var t=sa/(sa-sb);
                var at=new Point(a.X+(b.X-a.X)*t,a.Z+(b.Z-a.Z)*t);
                if (!Append(inside,ref insideCount,at) || !Append(outside,ref outsideCount,at)) return false;
            }
        }
        return true;
    }
    private bool Spend() => ++LastOperations<=MaximumOperations && (LastOperations%64!=0 || deadline?.Invoke()!=false);
    private static bool Append(Point[] destination,ref int count,Point point)
    {
        if (count>0 && destination[count-1]==point) return true;
        if (count>=MaximumVertices || !double.IsFinite(point.X) || !double.IsFinite(point.Z)) return false;
        destination[count++]=point; return true;
    }
    private static bool HasArea(ReadOnlySpan<Point> polygon)
    {
        if (polygon.Length<3) return false;
        double area=0,scale=0,perimeter=0;
        for(var i=0;i<polygon.Length;i++)
        {
            var p=polygon[i];var next=polygon[(i+1)%polygon.Length];
            scale=Math.Max(scale,Math.Max(Math.Abs(p.X),Math.Abs(p.Z)));
            perimeter+=Math.Abs(next.X-p.X)+Math.Abs(next.Z-p.Z);
        }
        for(var i=1;i+1<polygon.Length;i++) area+=Cross(polygon[i]-polygon[0],polygon[i+1]-polygon[0]);
        // Intersections carry rounded coordinates as well as rounded products.
        // A one-ULP-wide residual can have a non-cancelling determinant. Bound
        // that arithmetic uncertainty by local coordinate precision times its
        // perimeter; this is not a fixed world-space or area tolerance.
        return Math.Abs(area)>64*2.2204460492503131e-16*scale*perimeter;
    }
    private Point Local(Vector3 point) => new((double)point.X-origin.X,(double)point.Z-origin.Y);
    private static double Cross(Point a,Point b)
    {
        var first=a.X*b.Z;var second=a.Z*b.X;var value=first-second;
        // Repeated clipping creates rounded intersections on shared edges.
        // Their near-zero determinants otherwise leave fictitious positive
        // fragments. Bound cancellation by the operands' own precision, not a
        // world-space/area threshold which could fill a real narrow gap.
        const double rounding=16*2.2204460492503131e-16;
        return Math.Abs(value)<=rounding*(Math.Abs(first)+Math.Abs(second))?0:value;
    }
}
