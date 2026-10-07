using System.Numerics;

namespace XivCloth.Core;

/// <summary>Immutable exact-expression coefficients, not expanded/approximated
/// geometry. Constructor arithmetic matches the former per-query expressions;
/// dynamic point subtraction, clipping and threshold predicates stay unchanged.</summary>
internal readonly struct PreparedTriangle
{
    internal readonly D3 A,B,C,Normal;
    internal readonly D3 RawNormal;
    internal readonly MeasuredTriangle Source;
    private readonly D3 inward0,inward1,inward2;
    private readonly double ax,ay,bx,by,determinant;
    private readonly int axis;
    private readonly PreparedSeparationAxis planeSeparation,edgeSeparation0,edgeSeparation1,edgeSeparation2;
    private readonly PreparedSeparationAxis sweepX,sweepY,sweepZ,sweepNormal,sweepEdge0,sweepEdge1,sweepEdge2;
    internal PreparedTriangle(MeasuredTriangle triangle)
    {
        Source=triangle;A=new(triangle.A);B=new(triangle.B);C=new(triangle.C);
        var u=B-A;var v=C-A;var n=D3.Cross(u,v);Normal=n/Math.Sqrt(n.Squared);
        RawNormal=n;
        inward0=Inward(A,B,Normal);inward1=Inward(B,C,Normal);inward2=Inward(C,A,Normal);
        var nx=Math.Abs(u.Y*v.Z-u.Z*v.Y);var ny=Math.Abs(u.Z*v.X-u.X*v.Z);var nz=Math.Abs(u.X*v.Y-u.Y*v.X);
        if(nx>=ny&&nx>=nz){axis=0;ax=u.Y;ay=u.Z;bx=v.Y;by=v.Z;}
        else if(ny>=nz){axis=1;ax=u.X;ay=u.Z;bx=v.X;by=v.Z;}
        else{axis=2;ax=u.X;ay=u.Y;bx=v.X;by=v.Y;}
        determinant=ax*by-ay*bx;
        planeSeparation=new(Normal,A,B,C);edgeSeparation0=new(inward0,A,B,C);
        edgeSeparation1=new(inward1,A,B,C);edgeSeparation2=new(inward2,A,B,C);
        sweepX=new(new(1,0,0),A,B,C);sweepY=new(new(0,1,0),A,B,C);sweepZ=new(new(0,0,1),A,B,C);
        sweepNormal=new(n,A,B,C);sweepEdge0=new(D3.Cross(n,B-A),A,B,C);
        sweepEdge1=new(D3.Cross(n,C-B),A,B,C);sweepEdge2=new(D3.Cross(n,A-C),A,B,C);
    }
    private static D3 Inward(D3 from,D3 to,D3 normal)
    {var n=D3.Cross(normal,to-from);return n/Math.Sqrt(n.Squared);}
    internal D3 Vertex(int edge)=>edge==0?A:edge==1?B:C;
    internal D3 Inward(int edge)=>edge==0?inward0:edge==1?inward1:inward2;
    internal bool SeparatesFinite(MeasuredTriangle face,double distance)=>
        planeSeparation.ProvesSeparated(face,distance)||edgeSeparation0.ProvesSeparated(face,distance)
        ||edgeSeparation1.ProvesSeparated(face,distance)||edgeSeparation2.ProvesSeparated(face,distance);
    internal bool SweepFixedSeparated(ReadOnlySpan<R3> hull,double distance)=>
        sweepX.ProvesSeparated(hull,distance)||sweepY.ProvesSeparated(hull,distance)||sweepZ.ProvesSeparated(hull,distance)
        ||sweepNormal.ProvesSeparated(hull,distance)||sweepEdge0.ProvesSeparated(hull,distance)
        ||sweepEdge1.ProvesSeparated(hull,distance)||sweepEdge2.ProvesSeparated(hull,distance);
    internal bool ContainsProjected(Vector3 point)
    {
        var q=new D3(point)-A;double qx,qy;
        if(axis==0){qx=q.Y;qy=q.Z;}else if(axis==1){qx=q.X;qy=q.Z;}else{qx=q.X;qy=q.Y;}
        if(Math.Abs(determinant)<1e-20)return false;
        var u=(qx*by-qy*bx)/determinant;var v=(ax*qy-ay*qx)/determinant;
        return u>=-1e-6&&v>=-1e-6&&u+v<=1+1e-6;
    }
}
