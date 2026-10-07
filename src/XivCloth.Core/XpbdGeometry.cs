using System.Numerics;

namespace XivCloth.Core;

public enum MaterialEdge { Stretch, Shear }
public readonly record struct DistanceEdge(int A, int B, MaterialEdge Kind);
public readonly record struct MeasuredTriangle(Vector3 A, Vector3 B, Vector3 C);
internal readonly record struct Hinge(int OppositeA, int OppositeB, int EdgeA, int EdgeB, float RestAngle);
internal readonly record struct RestEdge(int A, int B, MaterialEdge Kind, float Length);

/// <summary>Immutable explicit 3-D support, with caller-supplied scene generation.
/// Default one-sided admission/endpoint support checks require winding into free
/// space; that extruded support prism is NOT swept. TwoSided explicitly selects
/// finite thin-sheet semantics, NOT automatic layer or solid classification.
/// Continuous certification is of finite faces only. This validates shape, NOT provenance,
/// floor classification, runtime freshness or suitability of a game model.</summary>
public sealed class MeasuredTriangleScene
{
    public const int MaximumTriangles = 128;
    private readonly MeasuredTriangle[] triangles;
    private readonly Vector3[] normals;
    private readonly Box3[] bounds;
    private readonly PreparedTriangle[] prepared;
    private readonly StaticTriangleTree tree;
    public bool TwoSided { get; }
    public long Generation { get; }
    public CollisionCoverage? Coverage { get; }
    public ReadOnlySpan<MeasuredTriangle> Triangles => triangles;
    public static MeasuredTriangleScene Empty { get; } = new(1, []);
    // Preserve the original CLR constructor for already compiled callers.
    public MeasuredTriangleScene(long generation, ReadOnlySpan<MeasuredTriangle> source,bool twoSided=false)
        :this(generation,source,twoSided,null) { }
    public MeasuredTriangleScene(long generation, ReadOnlySpan<MeasuredTriangle> source,bool twoSided,
        CollisionCoverage? coverage)
    {
        if(generation<=0 || source.Length>MaximumTriangles || coverage is { Valid:false })throw new ArgumentException("Invalid bounded scene.");
        Coverage=coverage;
        Generation=generation; TwoSided=twoSided; triangles=source.ToArray(); normals=new Vector3[source.Length];
        for(var i=0;i<triangles.Length;i++)
        {
            var t=triangles[i]; var cross=Vector3.Cross(t.B-t.A,t.C-t.A);
            if(!Geometry.Valid(t.A)||!Geometry.Valid(t.B)||!Geometry.Valid(t.C)||cross.LengthSquared()<1e-12f)
                throw new ArgumentException("Invalid measured triangle.");
            normals[i]=Vector3.Normalize(cross);
        }
        bounds=triangles.Select(Box3.Triangle).ToArray();
        prepared=triangles.Select(t=>new PreparedTriangle(t)).ToArray();tree=new(triangles);
    }
    internal bool Covers(Box3 box)=>Coverage is not {} volume || volume.Contains(box);
    internal int Query(Box3 box,Span<int> candidates,ref CollisionWork work)
    {
        var guard=GuardQuery(box,ref work);
        return guard<0?guard:tree.Query(box,candidates,ref work);
    }
    // -2 is unproved coverage; -1 is work exhaustion. Neither is an empty scene.
    internal int GuardQuery(Box3 box,ref CollisionWork work)
    {
        if(Coverage is null)return 0;
        if(!work.Charge(2))return -1;
        return Covers(box)?0:-2;
    }
    internal Box3 Bounds(int triangle)=>bounds[triangle];
    internal ref readonly PreparedTriangle Prepared(int triangle)=>ref prepared[triangle];

    // Discrete point/finite-face constraints, not infinite height planes.
    // A deeply crossed face is an explicit failure, not arbitrary depenetration.
    internal bool Project(ref Vector3 p,float thickness,float maximumPenetration,ref int checks,
        PlantContactDispatch? plant=null,int vertex=-1)
    {
        if(Coverage is not null&&!Covers(new Box3(p,p).Expand(maximumPenetration)))return false;
        var deferredDeep=false;
        for(var i=0;i<triangles.Length;i++)
        {
            checks++;var t=triangles[i];var normal=normals[i];
            var margin=plant?.PointMargin(vertex,i,thickness)??thickness;
            var signed=Vector3.Dot(p-t.A,normal);
            if(TwoSided&&signed<0){normal=-normal;signed=-signed;}
            if(signed>=margin)continue;
            var projected=p-signed*normal;
            if(!prepared[i].ContainsProjected(projected))continue;
            // A shallow tread correction may lift a point out of an adjacent
            // riser's blocked footprint. Do not let obstacle enumeration reject
            // that valid candidate before its tread was considered. Never apply
            // an arbitrarily deep correction: remember it for a bounded rescan.
            if(signed < -maximumPenetration){deferredDeep=true;continue;}
            p+=(margin-signed)*normal;
            if(Coverage is not null&&!Covers(new Box3(p,p).Expand(maximumPenetration)))return false;
        }
        if(deferredDeep)for(var i=0;i<triangles.Length;i++)
        {
            checks++;var t=triangles[i];var signed=Vector3.Dot(p-t.A,normals[i]);
            if(signed < -maximumPenetration&&prepared[i].ContainsProjected(p-signed*normals[i]))return false;
        }
        return true;
    }

    internal bool IsClear(Vector3 p,float thickness,ref int checks,PlantContactDispatch? plant=null,int vertex=-1)
    {
        if(Coverage is not null&&!Covers(new Box3(p,p).Expand(thickness)))return false;
        for(var i=0;i<triangles.Length;i++)
        {
            checks++;var t=triangles[i];var distance=Vector3.Dot(p-t.A,normals[i]);
            var margin=plant?.PointMargin(vertex,i,thickness)??thickness;
            if((TwoSided?Math.Abs(distance):distance)<(margin==0?0:margin-1e-5f)&&prepared[i].ContainsProjected(p-distance*normals[i]))return false;
        }
        return true;
    }

    public float MaximumVertexPenetration(ReadOnlySpan<Vector3> vertices)
    {
        // Diagnostic only; unknown captured coverage must not look like zero
        // penetration. This value is never an admission or sweep substitute.
        if(Coverage is not null)
            foreach(var p in vertices)if(!Covers(new Box3(p,p)))return float.PositiveInfinity;
        var result=0f;
        foreach(var p in vertices)for(var i=0;i<triangles.Length;i++)
        {
            var t=triangles[i];var signed=Vector3.Dot(p-t.A,normals[i]);
            if(signed<0 && prepared[i].ContainsProjected(p-signed*normals[i]))result=Math.Max(result,-signed);
        }
        return result;
    }
}

/// <summary>Particle-index topology, copied on admission. Zero inverse mass pins
/// to the admitted initial pose. There is no square-grid or fixed-XZ assumption.</summary>
public sealed class XpbdDefinition
{
    public const int MaximumVertices=512, MaximumIndices=3072, MaximumEdges=3072;
    internal Vector3[] Rest { get; }
    internal Vector2[] Texture { get; }
    internal int[] Faces { get; }
    internal float[] Weights { get; }
    internal RestEdge[] Edges { get; }
    internal Hinge[] Hinges { get; }
    public int VertexCount=>Rest.Length;
    public int StretchCount=>Edges.Count(e=>e.Kind==MaterialEdge.Stretch);
    public int ShearCount=>Edges.Count(e=>e.Kind==MaterialEdge.Shear);
    public int BendingCount=>Hinges.Length;
    public XpbdDefinition(ReadOnlySpan<Vector3> rest,ReadOnlySpan<Vector2> uv,ReadOnlySpan<int> indices,
        ReadOnlySpan<float> inverseMass,ReadOnlySpan<DistanceEdge> edges)
    {
        if(rest.Length is <3 or >MaximumVertices || uv.Length!=rest.Length || inverseMass.Length!=rest.Length
            || indices.Length is <3 or >MaximumIndices || indices.Length%3!=0 || edges.Length is <1 or >MaximumEdges)
            throw new ArgumentException("Invalid bounded indexed cloth.");
        Rest=rest.ToArray();Texture=uv.ToArray();Faces=indices.ToArray();Weights=inverseMass.ToArray();
        for(var i=0;i<Rest.Length;i++)
            if(!Geometry.Valid(Rest[i])||!float.IsFinite(Texture[i].X)||!float.IsFinite(Texture[i].Y)
                ||!float.IsFinite(Weights[i])||Weights[i]<0||Weights[i]>1000)
                throw new ArgumentException("Invalid particle or mass.");
        Edges=new RestEdge[edges.Length];var pairs=new HashSet<(int,int)>();
        for(var i=0;i<edges.Length;i++)
        {
            var e=edges[i];ValidateIndex(e.A);ValidateIndex(e.B);
            var key=(Math.Min(e.A,e.B),Math.Max(e.A,e.B));
            var length=Vector3.Distance(Rest[e.A],Rest[e.B]);
            if(e.A==e.B||!Enum.IsDefined(e.Kind)||!pairs.Add(key)||length is <.0001f or >32)
                throw new ArgumentException("Invalid or duplicate material edge.");
            Edges[i]=new(e.A,e.B,e.Kind,length);
        }
        var adjacent=new Dictionary<(int,int),(int A,int B,int Opposite,bool Paired)>();
        var hinges=new List<Hinge>();var faces=new HashSet<(int,int,int)>();
        for(var i=0;i<Faces.Length;i+=3)
        {
            var a=Faces[i];var b=Faces[i+1];var c=Faces[i+2];ValidateIndex(a);ValidateIndex(b);ValidateIndex(c);
            var low=Math.Min(a,Math.Min(b,c));var high=Math.Max(a,Math.Max(b,c));
            if(a==b||a==c||b==c||!faces.Add((low,a+b+c-low-high,high))
                ||Vector3.Cross(Rest[b]-Rest[a],Rest[c]-Rest[a]).LengthSquared()<1e-12f)
                throw new ArgumentException("Invalid or duplicate face.");
            Add(a,b,c);Add(b,c,a);Add(c,a,b);
        }
        Hinges=hinges.ToArray();
        void ValidateIndex(int i){if((uint)i>=(uint)Rest.Length)throw new ArgumentException("Invalid cloth index.");}
        void Add(int a,int b,int opposite)
        {
            var key=(Math.Min(a,b),Math.Max(a,b));
            if(!adjacent.TryGetValue(key,out var first)){adjacent.Add(key,(a,b,opposite,false));return;}
            if(first.Paired||first.A!=b||first.B!=a)throw new ArgumentException("Nonmanifold/inconsistently wound cloth.");
            if(!XpbdDihedral.Evaluate(Rest[first.Opposite],Rest[opposite],Rest[first.A],Rest[first.B],
                out var angle,out _,out _,out _,out _))throw new ArgumentException("Degenerate hinge.");
            hinges.Add(new(first.Opposite,opposite,first.A,first.B,angle));
            adjacent[key]=(first.A,first.B,first.Opposite,true);
        }
    }
}

/// <summary>Signed dihedral plus exact first derivatives by reverse chain rule
/// through normalized edge/face vectors and atan2. Not an opposite-vertex spring.
/// Convention: opposite vertices first, then oriented shared edge endpoints.</summary>
public static class XpbdDihedral
{
    public static bool Evaluate(Vector3 p0,Vector3 p1,Vector3 p2,Vector3 p3,out float angle,
        out Vector3 g0,out Vector3 g1,out Vector3 g2,out Vector3 g3)
    {
        angle=0;g0=g1=g2=g3=default;
        var edge=p3-p2;var u=Vector3.Cross(p2-p0,p3-p0);var v=Vector3.Cross(p3-p1,p2-p1);
        var length=edge.Length();var ul=u.Length();var vl=v.Length();
        if(!float.IsFinite(length+ul+vl)||length<1e-6f||ul<1e-8f||vl<1e-8f)return false;
        var tangent=edge/length;var n=u/ul;var m=v/vl;var cross=Vector3.Cross(n,m);
        var x=Vector3.Dot(n,m);var y=Vector3.Dot(tangent,cross);var denominator=x*x+y*y;
        if(denominator<1e-10f)return false;
        angle=MathF.Atan2(y,x);
        var dx=-y/denominator;var dy=x/denominator;
        var gn=dx*m+dy*Vector3.Cross(m,tangent);
        var gm=dx*n+dy*Vector3.Cross(tangent,n);
        var gt=dy*cross;
        var gu=(gn-n*Vector3.Dot(n,gn))/ul;
        var gv=(gm-m*Vector3.Dot(m,gm))/vl;
        var ge=(gt-tangent*Vector3.Dot(tangent,gt))/length;
        var a=Vector3.Cross(p3-p0,gu);var b=Vector3.Cross(gu,p2-p0);
        var c=Vector3.Cross(p2-p1,gv);var d=Vector3.Cross(gv,p3-p1);
        g0=-a-b;g1=-c-d;g2=a+d-ge;g3=b+c+ge;
        return Geometry.Finite(g0)&&Geometry.Finite(g1)&&Geometry.Finite(g2)&&Geometry.Finite(g3);
    }
    public static float Wrap(float angle)=>MathF.Atan2(MathF.Sin(angle),MathF.Cos(angle));
}

internal static class Geometry
{
    public static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
    public static bool Valid(Vector3 p)=>Finite(p)&&Math.Max(Math.Max(Math.Abs(p.X),Math.Abs(p.Y)),Math.Abs(p.Z))<=10000;
    public static bool InTriangle(Vector3 p,MeasuredTriangle t)
    {
        // Componentwise widening BEFORE subtraction/multiplication. A float
        // Gram determinant can cancel to zero for an admitted skinny triangle;
        // even its double counterpart unnecessarily squares that conditioning.
        double ax=(double)t.B.X-t.A.X,ay=(double)t.B.Y-t.A.Y,az=(double)t.B.Z-t.A.Z;
        double bx=(double)t.C.X-t.A.X,by=(double)t.C.Y-t.A.Y,bz=(double)t.C.Z-t.A.Z;
        double qx=(double)p.X-t.A.X,qy=(double)p.Y-t.A.Y,qz=(double)p.Z-t.A.Z;
        var nx=Math.Abs(ay*bz-az*by);var ny=Math.Abs(az*bx-ax*bz);var nz=Math.Abs(ax*by-ay*bx);
        if(nx>=ny&&nx>=nz){ax=ay;ay=az;bx=by;by=bz;qx=qy;qy=qz;}
        else if(ny>=nz){ay=az;by=bz;qy=qz;}
        var determinant=ax*by-ay*bx;if(Math.Abs(determinant)<1e-20)return false;
        var u=(qx*by-qy*bx)/determinant;var v=(ax*qy-ay*qx)/determinant;
        return u>=-1e-6&&v>=-1e-6&&u+v<=1+1e-6;
    }
}
