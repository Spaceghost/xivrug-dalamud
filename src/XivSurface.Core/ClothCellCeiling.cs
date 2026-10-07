using System.Numerics;

namespace XivSurface.Core;

/// <summary>Bounds the conservative hull of a complete material cell using actual local floor geometry.
/// The caller must supply fresh faces reached through portals inside the cell,
/// starting at its measured center. Global graph membership is insufficient:
/// an unrelated deck connected by a distant ramp must not enter this set.
/// A successful result covers the whole footprint and its known face heights;
/// it cannot establish the existence or absence of unobserved scene geometry.</summary>
public sealed class ClothCellCeiling
{
    public const int MaximumTriangles = CoplanarCellCoverage.MaximumTriangles;
    public const int MaximumOperations = 2 * CoplanarCellCoverage.MaximumOperations;
    private const int PolygonCapacity = 16;
    private readonly record struct Point(double X, double Y, double Z)
    {
        public static Point operator -(Point a, Point b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    }
    private readonly record struct Face(LayerTriangle Triangle,
        double MinX, double MaxX, double MinZ, double MaxZ);
    private readonly Face[] faces = new Face[MaximumTriangles];
    private readonly List<LayerTriangle> projected = new(MaximumTriangles);
    private readonly CoplanarCellCoverage coverage = new();
    private Func<bool>? deadline;
    private int operations;
    private bool coverageRan;
    public int LastOperations { get; private set; }
    public string LastFailure { get; private set; } = "Not measured";
    public Vector2? MissingWitness { get; private set; }
    public double MissingArea { get; private set; }
    public int LastFaceCount { get; private set; }
    public int LastCornerMask { get; private set; }
    public double LastOverlapDelta { get; private set; }
    /// <summary>Proved nonnegative height to add to each actual corner's own
    /// floor height. Covers both renderer diagonals. NaN until full success.</summary>
    public float LastLift { get; private set; } = float.NaN;

    public LayerQueryResult Measure(LayerFloorHit center, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        IReadOnlyList<LayerTriangle> localFaces, out float ceiling, Func<bool>? withinDeadline = null)
        => MeasureSupport(center,a,b,c,d,localFaces,out ceiling,null,withinDeadline);

    // The caller supplies only explicitly admitted cloth-role faces. Public
    // Measure retains ordinary walkable semantics and its CLR signature.
    internal LayerQueryResult MeasureSupport(LayerFloorHit center, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        IReadOnlyList<LayerTriangle> localFaces,out float ceiling,Func<LayerTriangle,bool>? connector,
        Func<bool>? withinDeadline=null)
    {
        ArgumentNullException.ThrowIfNull(localFaces);
        ceiling = 0; operations = LastOperations = 0; coverageRan = false; deadline = withinDeadline;
        LastFailure=""; MissingWitness=null; MissingArea=0; LastFaceCount=LastCornerMask=0; LastOverlapDelta=0; LastLift=float.NaN;
        if (deadline?.Invoke() == false) return Failed("Deadline");
        if (!MathEx.Finite(center.Position) || !ClothConnectorGeometry.Contains(center.Triangle,center.Position,connector?.Invoke(center.Triangle)==true)
            || localFaces.Count is < 1 or > MaximumTriangles) return Reject("Invalid center or face count");
        var origin = new Vector2(a.X,a.Z);
        Span<Point> cell = stackalloc Point[4];
        if (!Cell(a,b,c,d,cell,out var orientation,out var cellCount)) return Reject("Collapsed or invalid footprint");
        Span<Point> clipped = stackalloc Point[PolygonCapacity];
        Span<Point> reference = stackalloc Point[]
        {
            Local(a,origin),Local(b,origin),Local(c,origin), Local(b,origin),Local(d,origin),Local(c,origin),
            Local(a,origin),Local(b,origin),Local(d,origin), Local(a,origin),Local(d,origin),Local(c,origin),
        };
        projected.Clear();
        var faceCount = 0; var hasCenter = false; var maximum = double.NegativeInfinity; var maximumLift=0d;
        Span<bool> cornerCovered = stackalloc bool[4]; cornerCovered.Clear();
        Span<Vector3> corners = stackalloc Vector3[] { a,b,c,d };
        foreach (var triangle in localFaces)
        {
            if (!Spend()) return Failed("Geometry operation limit");
            var clothRole=connector?.Invoke(triangle)==true;
            if (!triangle.Walkable && (!clothRole||!ClothConnectorGeometry.Eligible(triangle))) return Reject("Invalid walkable face");
            hasCenter |= triangle == center.Triangle;
            for (var i=0;i<4;i++) cornerCovered[i] |= ClothConnectorGeometry.Contains(triangle,corners[i],clothRole);
            var count = ClipToCell(triangle,origin,cell[..cellCount],orientation,clipped);
            if (count < 0) return Failed("Clipping operation limit");
            if (!HasArea(clipped[..count])) continue;
            // The source floor and contact triangle are each affine. Their
            // difference reaches its maximum at an intersection vertex. Test
            // both diagonal splits so the scalar lift is valid for either
            // checkerboard orientation used by the material lattice.
            for(var split=0;split<4;split++)
            {
                if(!ResidualLift(clipped[..count],reference.Slice(split*3,3),out var residual))
                    return Failed("Contact-plane intersection limit");
                maximumLift=Math.Max(maximumLift,residual);
            }
            var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
            var minZ = double.PositiveInfinity; var maxZ = double.NegativeInfinity;
            for(var i=0;i<count;i++)
            {
                var point=clipped[i];
                maximum=Math.Max(maximum,point.Y);
                minX=Math.Min(minX,point.X); maxX=Math.Max(maxX,point.X);
                minZ=Math.Min(minZ,point.Z); maxZ=Math.Max(maxZ,point.Z);
            }
            // Height is affine on each actual triangle: its clipped polygon
            // vertices give the exact maximum, including an off-center peak.
            var face = new Face(triangle,minX,maxX,minZ,maxZ);
            for(var previous=0;previous<faceCount;previous++)
            {
                if (!Spend()) return Failed("Overlap operation limit");
                var other=faces[previous];
                if (other.MaxX<=minX || other.MinX>=maxX || other.MaxZ<=minZ || other.MinZ>=maxZ) continue;
                if (!AgreeOnOverlap(clipped[..count],other.Triangle,origin,out var agree)) return Failed("Overlap clipping limit");
                if (!agree) return Reject("Overlapping floors have different heights");
            }
            faces[faceCount++]=face;
            LastFaceCount=faceCount;
            projected.Add(Flatten(triangle));
        }
        if (!hasCenter || faceCount==0 || !double.IsFinite(maximum)) return Reject("Measured center is absent from clipped faces");
        for(var i=0;i<cornerCovered.Length;i++) if(cornerCovered[i])LastCornerMask|=1<<i;
        // Reuse the existing polygon-subtraction proof in XZ. Flattening is
        // only a 2D coverage calculation; the ceiling above came from original
        // world triangles, and differing overlapping heights were rejected.
        coverageRan=true;
        var flatCenter=new LayerFloorHit(Flat(center.Position),Flatten(center.Triangle));
        if(!coverage.Proves(flatCenter,Flat(a),Flat(b),Flat(c),Flat(d),projected,deadline))
        { MissingWitness=coverage.MissingWitness; MissingArea=coverage.MissingArea; return Failed("Uncovered footprint"); }
        foreach(var covered in cornerCovered) if(!covered) return Reject("Measured corner is absent from local faces");
        ceiling=(float)maximum;
        if((double)ceiling<maximum) ceiling=MathF.BitIncrement(ceiling);
        if(!float.IsFinite(ceiling)) { ceiling=0; return Reject("Nonfinite ceiling"); }
        var lift=(float)maximumLift;
        if((double)lift<maximumLift)lift=MathF.BitIncrement(lift);
        if(!float.IsFinite(lift)||lift<0){ceiling=0;return Reject("Nonfinite residual lift");}
        LastLift=lift;
        return Finish(LayerQueryResult.Success);

        LayerQueryResult Failed(string reason)
        {
            var pending=deadline?.Invoke()==false;
            LastFailure=pending?"Deadline":reason;
            if(pending)MissingWitness=null;
            return Finish(pending?LayerQueryResult.Pending:LayerQueryResult.Unknown);
        }
        LayerQueryResult Reject(string reason) { LastFailure=reason;return Finish(LayerQueryResult.Unknown); }
    }

    /// <summary>A portal must have a positive-length part strictly inside the
    /// conservative convex footprint of the measured corners. Touching its outside boundary or a corner cannot
    /// authorize a detour out of the cell and onto an overlapping storey.</summary>
    public static bool PortalIntersectsCell(Vector3 portalA, Vector3 portalB,
        Vector3 a,Vector3 b,Vector3 c,Vector3 d)
    {
        if(!MathEx.Finite(portalA)||!MathEx.Finite(portalB))return false;
        Span<Point> cell=stackalloc Point[4];
        if(!Cell(a,b,c,d,cell,out var orientation,out var cellCount))return false;
        var origin=new Vector2(a.X,a.Z); var from=Local(portalA,origin);var to=Local(portalB,origin);
        if(from.X==to.X&&from.Z==to.Z)return false;
        double low=0,high=1;
        for(var i=0;i<cellCount;i++)
        {
            var edge=cell[(i+1)%cellCount]-cell[i];
            var start=orientation*Cross(edge,from-cell[i]);var end=orientation*Cross(edge,to-cell[i]);
            var change=end-start;
            if(change==0) { if(start<=0)return false; continue; }
            var crossing=-start/change;
            if(change>0)low=Math.Max(low,crossing);else high=Math.Min(high,crossing);
            if(low>=high)return false;
        }
        return low<high;
    }

    private int ClipToCell(LayerTriangle triangle,Vector2 origin,ReadOnlySpan<Point> cell,int orientation,Span<Point> destination)
    {
        Span<Point> current=stackalloc Point[PolygonCapacity];Span<Point> next=stackalloc Point[PolygonCapacity];
        current[0]=Local(triangle.A,origin);current[1]=Local(triangle.B,origin);current[2]=Local(triangle.C,origin);
        var count=3;
        for(var i=0;i<cell.Length&&count>0;i++)
        {
            var nextCount=Clip(current[..count],cell[i],cell[(i+1)%cell.Length],orientation,next);
            if(nextCount<0)return -1;
            next[..nextCount].CopyTo(current);count=nextCount;
        }
        current[..count].CopyTo(destination);return count;
    }

    private bool AgreeOnOverlap(ReadOnlySpan<Point> polygon,LayerTriangle other,Vector2 origin,out bool agree)
    {
        agree=false;
        Span<Point> current=stackalloc Point[PolygonCapacity];Span<Point> next=stackalloc Point[PolygonCapacity];
        polygon.CopyTo(current);var count=polygon.Length;
        Span<Point> edges=stackalloc Point[] { Local(other.A,origin),Local(other.B,origin),Local(other.C,origin) };
        var orientation=Math.Sign(Cross(edges[1]-edges[0],edges[2]-edges[0]));
        for(var i=0;i<3&&count>0;i++)
        {
            var nextCount=Clip(current[..count],edges[i],edges[(i+1)%3],orientation,next);
            if(nextCount<0)return false;
            next[..nextCount].CopyTo(current);count=nextCount;
        }
        if(!HasArea(current[..count])) { agree=true;return true; }
        var ab=edges[1]-edges[0];var ac=edges[2]-edges[0];var determinant=Cross(ab,ac);
        for(var i=0;i<count;i++)
        {
            if(!Spend())return false;
            var delta=current[i]-edges[0];
            var u=Cross(delta,ac)/determinant;var v=Cross(ab,delta)/determinant;
            var otherY=edges[0].Y+ab.Y*u+ac.Y*v;
            if(!double.IsFinite(otherY)||Math.Abs(otherY-current[i].Y)>LocalFloorLayer.SeamTolerance)
            { LastOverlapDelta=Math.Abs(otherY-current[i].Y);return true; }
        }
        agree=true;return true;
    }

    private bool ResidualLift(ReadOnlySpan<Point> polygon,ReadOnlySpan<Point> contactTriangle,out double residual)
    {
        residual=0;
        Span<Point> current=stackalloc Point[PolygonCapacity];Span<Point> next=stackalloc Point[PolygonCapacity];
        polygon.CopyTo(current);var count=polygon.Length;
        var ab=contactTriangle[1]-contactTriangle[0];var ac=contactTriangle[2]-contactTriangle[0];
        var determinant=Cross(ab,ac);
        if(!double.IsFinite(determinant))return false;
        // A zero-area split adds no XZ interior. Its edges are still bounded
        // by the other nondegenerate splits of the same measured hull.
        if(determinant==0)return true;
        var orientation=Math.Sign(determinant);
        for(var edge=0;edge<3&&count>0;edge++)
        {
            var nextCount=Clip(current[..count],contactTriangle[edge],contactTriangle[(edge+1)%3],orientation,next);
            if(nextCount<0)return false;
            next[..nextCount].CopyTo(current);count=nextCount;
        }
        for(var i=0;i<count;i++)
        {
            if(!Spend())return false;
            var delta=current[i]-contactTriangle[0];
            var u=Cross(delta,ac)/determinant;var v=Cross(ab,delta)/determinant;
            var contactY=contactTriangle[0].Y+ab.Y*u+ac.Y*v;
            var difference=current[i].Y-contactY;
            if(!double.IsFinite(difference))return false;
            residual=Math.Max(residual,difference);
        }
        return true;
    }

    private int Clip(ReadOnlySpan<Point> polygon,Point from,Point to,int orientation,Span<Point> destination)
    {
        var count=0;var edge=to-from;
        for(var i=0;i<polygon.Length;i++)
        {
            if(!Spend())return -1;
            var a=polygon[i];var b=polygon[(i+1)%polygon.Length];
            var sa=orientation*Cross(edge,a-from);var sb=orientation*Cross(edge,b-from);
            if(sa>=0&&!Append(destination,ref count,a))return -1;
            if(sa<0&&sb>0||sa>0&&sb<0)
            {
                var t=sa/(sa-sb);var p=new Point(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,a.Z+(b.Z-a.Z)*t);
                if(!Append(destination,ref count,p))return -1;
            }
        }
        if(count>1&&destination[0].X==destination[count-1].X&&destination[0].Z==destination[count-1].Z)count--;
        return count;
    }

    private static bool Cell(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Span<Point> points,out int orientation,out int count)
    {
        Span<Vector3> hull=stackalloc Vector3[4];
        count=ClothContactFootprint.Hull(a,b,c,d,hull);orientation=1;
        if(count<3)return false;
        var origin=new Vector2(a.X,a.Z);
        for(var i=0;i<count;i++)points[i]=Local(hull[i],origin);
        return true;
    }
    private bool Spend()=>++operations<=MaximumOperations/2&&(operations%64!=0||deadline?.Invoke()!=false);
    private LayerQueryResult Finish(LayerQueryResult result)
    { LastOperations=operations+(coverageRan?coverage.LastOperations:0);return result; }
    private static bool Append(Span<Point> destination,ref int count,Point point)
    {
        if(!double.IsFinite(point.X)||!double.IsFinite(point.Y)||!double.IsFinite(point.Z))return false;
        if(count>0&&destination[count-1].X==point.X&&destination[count-1].Z==point.Z)return true;
        if(count>=destination.Length)return false;destination[count++]=point;return true;
    }
    private static bool HasArea(ReadOnlySpan<Point> polygon)
    {
        double area=0;for(var i=1;i+1<polygon.Length;i++)area+=Cross(polygon[i]-polygon[0],polygon[i+1]-polygon[0]);
        return area!=0&&double.IsFinite(area);
    }
    private static Point Local(Vector3 point,Vector2 origin)=>new((double)point.X-origin.X,point.Y,(double)point.Z-origin.Y);
    private static double Cross(Point a,Point b)=>a.X*b.Z-a.Z*b.X;
    private static Vector3 Flat(Vector3 point)=>new(point.X,0,point.Z);
    private static LayerTriangle Flatten(LayerTriangle face)=>new(Flat(face.A),Flat(face.B),Flat(face.C),Vector3.UnitY);
}
