using System.Numerics;

namespace XivSurface.Core;

public enum CarpetFootworkPhase { Walking, Smoothing, Complete }
public readonly record struct CarpetFootworkSample(Vector3 Offset, float Yaw, CarpetFootworkPhase Phase);

/// <summary>A short visual excursion over the currently measured cloth. The
/// positions are draw offsets only; this class never requests gameplay motion.</summary>
public sealed class CarpetFootworkPlan
{
    public const float MaximumRadius = .85f, FootRadius = .18f, WalkingSpeed = .7f;
    private readonly record struct Leg(Vector3[] Points, float Length, double Starts, bool Returning);
    private readonly Leg[] legs;
    private readonly Vector3 anchor;
    private readonly float anchorYaw;
    private readonly double stompSeconds;
    private int[] sourceIndices, supportIndices;
    public double Duration { get; }
    public int TargetCount => legs.Length / 2;
    private CarpetFootworkPlan(Vector3 anchor, float yaw, Leg[] legs, double duration, double stomp, int[] source, int[] local)
    { this.anchor=anchor;anchorYaw=yaw;this.legs=legs;Duration=duration;stompSeconds=stomp;sourceIndices=source;supportIndices=local; }

    public static CarpetFootworkPlan? Create(ClothMesh? cloth, Vector3 anchor, float yaw, double stompSeconds)
    {
        if(!Valid(cloth)||!MathEx.Finite(anchor)||!float.IsFinite(yaw)||!double.IsFinite(stompSeconds))return null;
        stompSeconds=Math.Clamp(stompSeconds,1,12);
        var original=cloth!;var mesh=original with {Indices=LocalIndices(original,anchor)};var candidates=new List<(int Index,float Score)>();
        for(var i=0;i<mesh.Positions.Length;i++)
        {
            var p=mesh.Positions[i];var distance=Vector2.Distance(new(p.X,p.Z),new(anchor.X,anchor.Z));
            var fold=p.Y-mesh.GroundMinimum![i]-ClothSurface.Clearance;
            if(!MathEx.Finite(p)||!float.IsFinite(fold)||fold<.012f||distance<.28f||distance>MaximumRadius)continue;
            candidates.Add((i,fold-.015f*distance));
        }
        candidates.Sort((a,b)=>b.Score.CompareTo(a.Score));
        var selected=new List<Vector3>();var built=new List<Leg>();var duration=0d;var tried=0;
        foreach(var candidate in candidates)
        {
            var target=mesh.Positions[candidate.Index];
            if(selected.Any(p=>Vector2.DistanceSquared(new(p.X,p.Z),new(target.X,target.Z))<.16f))continue;
            if(++tried>12)break;
            if(!TraceFootprint(mesh,anchor,target,out var path))continue;
            var length=Length(path);if(length<.25f||length>1.1f||duration+2*length/WalkingSpeed+stompSeconds>30)continue;
            built.Add(new(path,length,duration,false));duration+=length/WalkingSpeed+stompSeconds;
            var back=path.Reverse().ToArray();built.Add(new(back,length,duration,true));duration+=length/WalkingSpeed;
            selected.Add(target);if(selected.Count==2)break;
        }
        return built.Count==0?null:new(anchor,yaw,built.ToArray(),duration,stompSeconds,original.Indices,mesh.Indices);
    }

    public CarpetFootworkSample Sample(double elapsed)
    {
        if(!double.IsFinite(elapsed)||elapsed>=Duration)return new(Vector3.Zero,anchorYaw,CarpetFootworkPhase.Complete);
        elapsed=Math.Max(0,elapsed);
        foreach(var leg in legs)
        {
            var walking=leg.Length/WalkingSpeed;var end=leg.Starts+walking;
            if(elapsed>=end+(leg.Returning?0:stompSeconds))continue;
            var smoothing=elapsed>=end;var distance=(float)Math.Clamp((elapsed-leg.Starts)*WalkingSpeed,0,leg.Length);
            for(var i=1;i<leg.Points.Length;i++)
            {
                var delta=leg.Points[i]-leg.Points[i-1];var segment=delta.Length();
                if(distance>segment&&i+1<leg.Points.Length){distance-=segment;continue;}
                var at=Vector3.Lerp(leg.Points[i-1],leg.Points[i],segment>0?Math.Clamp(distance/segment,0,1):1);
                return new(at-anchor,MathF.Atan2(delta.X,delta.Z),smoothing?CarpetFootworkPhase.Smoothing:CarpetFootworkPhase.Walking);
            }
        }
        return new(Vector3.Zero,anchorYaw,CarpetFootworkPhase.Complete);
    }

    /// <summary>Rechecks the actual support at the displayed feet. Losing the
    /// mesh or a changed storey cancels the excursion rather than using old Y.</summary>
    public bool StillSupported(ClothMesh? cloth, Vector3 offset)
    {
        if(!Valid(cloth)||!MathEx.Finite(offset))return false;
        if(!ReferenceEquals(sourceIndices,cloth!.Indices))
        { sourceIndices=cloth.Indices;supportIndices=LocalIndices(cloth,anchor); }
        var at=anchor+offset;
        for(var i=0;i<9;i++)
        {
            var shift=i==0?Vector2.Zero:new Vector2(MathF.Sin(i*MathF.Tau/8),MathF.Cos(i*MathF.Tau/8))*FootRadius;
            if(!TryHeight(cloth!,supportIndices,new(at.X+shift.X,at.Z+shift.Y),out var y)||Math.Abs(y-at.Y)>.12f)return false;
        }
        return true;
    }

    private static bool TraceFootprint(ClothMesh mesh,Vector3 from,Vector3 to,out Vector3[] path)
    {
        path=[];
        for(var i=0;i<9;i++)
        {
            var shift=i==0?Vector2.Zero:new Vector2(MathF.Sin(i*MathF.Tau/8),MathF.Cos(i*MathF.Tau/8))*FootRadius;
            if(!Trace(mesh,new(from.X+shift.X,from.Z+shift.Y),new(to.X+shift.X,to.Z+shift.Y),from.Y,out var points))return false;
            if(i==0)path=points;
        }
        // GroundMinimum already excludes textile clearance. The supported
        // path begins at the game's planted feet without adding that clearance.
        return path.Length>=2&&Vector3.Distance(path[0],from)<.08f;
    }
    private readonly record struct Interval(double Start,double End,double Y0,double Y1);
    private static bool Trace(ClothMesh mesh,Vector2 from,Vector2 to,float anchorY,out Vector3[] path)
    {
        path=[];var intervals=new List<Interval>();
        for(var i=0;i<mesh.Indices.Length;i+=3)
        {
            if(!Triangle(mesh,mesh.Indices,i,out var a,out var b,out var c))continue;
            var determinant=Cross(b-a,c-a);var p=new Vector3(from.X,0,from.Y);var q=new Vector3(to.X,0,to.Y);
            var u0=Cross(p-a,c-a)/determinant;var v0=Cross(b-a,p-a)/determinant;
            var u1=Cross(q-a,c-a)/determinant;var v1=Cross(b-a,q-a)/determinant;
            double low=0,high=1;
            if(!Clip(u0,u1,ref low,ref high)||!Clip(v0,v1,ref low,ref high)||!Clip(1-u0-v0,1-u1-v1,ref low,ref high)||high-low<1e-7)continue;
            var y0=a.Y+(b.Y-a.Y)*u0+(c.Y-a.Y)*v0;var y1=a.Y+(b.Y-a.Y)*u1+(c.Y-a.Y)*v1;
            foreach(var other in intervals)
            {
                var start=Math.Max(low,other.Start);var end=Math.Min(high,other.End);
                if(end-start<=1e-7)continue;
                double Difference(double t)=>y0+(y1-y0)*t-other.Y0-(other.Y1-other.Y0)*t;
                if(Math.Abs(Difference(start))>.025||Math.Abs(Difference(end))>.025)return false;
            }
            intervals.Add(new(low,high,y0,y1));
        }
        intervals.Sort((a,b)=>a.Start.CompareTo(b.Start));
        var points=new List<Vector3>();double covered=0;var lastY=double.NaN;
        foreach(var interval in intervals)
        {
            if(interval.Start>covered+1e-6)break;
            if(interval.End<=covered+1e-7)continue;
            var startY=interval.Y0+(interval.Y1-interval.Y0)*covered;
            if(Math.Abs(startY-anchorY)>.25||double.IsFinite(lastY)&&Math.Abs(startY-lastY)>.025)return false;
            if(points.Count==0)points.Add(new(from.X,(float)startY,from.Y));
            var endY=interval.Y0+(interval.Y1-interval.Y0)*interval.End;
            if(Math.Abs(endY-anchorY)>.25)return false;
            var xz=Vector2.Lerp(from,to,(float)interval.End);points.Add(new(xz.X,(float)endY,xz.Y));
            covered=interval.End;lastY=endY;
            if(covered>=1-1e-6){path=points.ToArray();return true;}
        }
        return false;
    }
    private static bool Clip(double a,double b,ref double low,ref double high)
    {
        var delta=b-a;if(Math.Abs(delta)<1e-15)return a>=-1e-7;
        var crossing=-a/delta;if(delta>0)low=Math.Max(low,crossing);else high=Math.Min(high,crossing);
        return low<=high;
    }
    private static bool TryHeight(ClothMesh mesh,int[] indices,Vector2 point,out float height)
    {
        height=0;var found=false;var p=new Vector3(point.X,0,point.Y);
        for(var i=0;i<indices.Length;i+=3)
        {
            if(!Triangle(mesh,indices,i,out var a,out var b,out var c))continue;
            var determinant=Cross(b-a,c-a);var u=Cross(p-a,c-a)/determinant;var v=Cross(b-a,p-a)/determinant;
            if(u< -1e-6||v< -1e-6||u+v>1+1e-6)continue;
            var y=(float)(a.Y+(b.Y-a.Y)*u+(c.Y-a.Y)*v);
            if(found&&Math.Abs(y-height)>.025)return false;
            height=y;found=true;
        }
        return found;
    }
    private static bool Triangle(ClothMesh mesh,int[] indices,int index,out Vector3 a,out Vector3 b,out Vector3 c)
    {
        a=b=c=default;var ia=indices[index];var ib=indices[index+1];var ic=indices[index+2];
        if((uint)ia>=mesh.Positions.Length||(uint)ib>=mesh.Positions.Length||(uint)ic>=mesh.Positions.Length)return false;
        a=mesh.Positions[ia];b=mesh.Positions[ib];c=mesh.Positions[ic];
        a.Y=mesh.GroundMinimum![ia];b.Y=mesh.GroundMinimum[ib];c.Y=mesh.GroundMinimum[ic];
        if(!MathEx.Finite(a)||!MathEx.Finite(b)||!MathEx.Finite(c))return false;
        var normal=Vector3.Cross(b-a,c-a);var length=normal.Length();
        return length>1e-8f&&Math.Abs(normal.Y)/length>.85f;
    }
    private static int[] LocalIndices(ClothMesh mesh,Vector3 anchor)
    {
        const float reach=MaximumRadius+FootRadius+.1f;
        var indices=new List<int>();
        for(var i=0;i<mesh.Indices.Length;i+=3)
        {
            var ia=mesh.Indices[i];var ib=mesh.Indices[i+1];var ic=mesh.Indices[i+2];
            if((uint)ia>=mesh.Positions.Length||(uint)ib>=mesh.Positions.Length||(uint)ic>=mesh.Positions.Length)continue;
            var a=mesh.Positions[ia];var b=mesh.Positions[ib];var c=mesh.Positions[ic];
            if(Math.Min(a.X,Math.Min(b.X,c.X))>anchor.X+reach||Math.Max(a.X,Math.Max(b.X,c.X))<anchor.X-reach||
                Math.Min(a.Z,Math.Min(b.Z,c.Z))>anchor.Z+reach||Math.Max(a.Z,Math.Max(b.Z,c.Z))<anchor.Z-reach)continue;
            indices.Add(ia);indices.Add(ib);indices.Add(ic);
        }
        return indices.ToArray();
    }
    private static bool Valid(ClothMesh? mesh)=>mesh is not null&&mesh.Positions.Length<=ClothSurface.MaximumSize*ClothSurface.MaximumSize&&mesh.GroundMinimum?.Length==mesh.Positions.Length&&mesh.Indices.Length is >0 and <=98304&&mesh.Indices.Length%3==0;
    private static double Cross(Vector3 a,Vector3 b)=>(double)a.X*b.Z-(double)a.Z*b.X;
    private static float Length(Vector3[] points){float length=0;for(var i=1;i<points.Length;i++)length+=Vector3.Distance(points[i-1],points[i]);return length;}
}
