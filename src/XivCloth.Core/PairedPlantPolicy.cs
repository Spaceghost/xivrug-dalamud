using System.Numerics;

namespace XivCloth.Core;

/// <summary>An explicit separate approximate contact model. The actual capsule
/// observations are retained unchanged. For a paired foot only, its endpoint
/// height is represented as tread+radius+the observed axis displacement from
/// the calibrated reference. This avoids manufacturing a positive sole gap
/// from float addition/subtraction; it is NOT exact shoe geometry.</summary>
public sealed class PairedPlantPolicy
{
    internal XpbdDefinition Definition { get; }
    public MeasuredTriangleScene Scene { get; }
    public FootProxyPose Reference { get; }
    public bool MatchesReferenceModel(object source)=>Reference.Identity.ModelBinding?.Matches(source)==true;
    internal readonly PlantTread[] Treads;
    private PairedPlantPolicy(XpbdDefinition definition,MeasuredTriangleScene scene,
        FootProxyPose reference,PlantTread[] treads)
    {Definition=definition;Scene=scene;Reference=reference;Treads=treads;}

    /// <summary>Both feet are explicitly paired. Each tread is one triangle or
    /// an exact convex union of two upward horizontal triangles sharing their
    /// entire edge. The immutable scene is retained, NEVER filtered/rebuilt.</summary>
    public static bool TryCreate(XpbdDefinition definition,MeasuredTriangleScene scene,
        FootProxyPose reference,ReadOnlySpan<int> leftTread,ReadOnlySpan<int> rightTread,
        out PairedPlantPolicy? policy)
    {
        policy=null;
        if(definition is null||scene is null||reference is null||scene.Coverage is null
            ||reference.Identity.ModelBinding is null||reference.CaptureBinding is null
            ||!PlantTread.TryCreate(scene,leftTread,out var left)||!PlantTread.TryCreate(scene,rightTread,out var right))return false;
        PlantTread[] treads=[left!,right!];
        for(var i=0;i<2;i++)
        {
            var foot=reference.Capsules[i];var tread=treads[i];
            // This is eligibility for a DECLARED plane-parametric model, not
            // penetration tolerance in its contact certificate. Its subsequent
            // displacement must be nonnegative with no epsilon or clamping.
            if(Math.Abs((double)foot.A.Y-foot.Radius-tread.Height)>1e-5
                ||Math.Abs((double)foot.B.Y-foot.Radius-tread.Height)>1e-5
                ||!tread.Contains(foot.A,foot.Radius)||!tread.Contains(foot.B,foot.Radius))return false;
        }
        policy=new(definition,scene,reference,treads);return true;
    }

    public bool TryObserve(FootProxyPose actual,double now,out PairedPlantObservation? observation)
    {
        observation=null;
        if(actual is null||actual.Identity!=Reference.Identity||actual.CaptureBinding is null
            ||!actual.FreshAt(now)||actual.Sequence<Reference.Sequence
            ||actual.Sequence==Reference.Sequence&&!ReferenceEquals(actual,Reference))return false;
        for(var f=0;f<2;f++)
        {
            var a=actual.Capsules[f];var b=Reference.Capsules[f];
            if(a.Radius!=b.Radius||(double)a.A.Y-b.A.Y<0||(double)a.B.Y-b.B.Y<0)return false;
            // Unlike the old prototype, unequal endpoint lift and rotation are
            // not discarded. Actual X/Z and the original full capsules survive.
        }
        observation=new(this,actual);return true;
    }

    internal double Sole(FootProxyPose pose,int foot,bool toe)
    {
        var actual=pose.Capsules[foot];var initial=Reference.Capsules[foot];
        return (double)Treads[foot].Height+(toe?(double)actual.B.Y-initial.B.Y:(double)actual.A.Y-initial.A.Y);
    }
    public bool NamesSupport(int foot,MeasuredTriangle triangle)
    {
        if(foot is <0 or >1)return false;
        foreach(var index in Treads[foot].Indices)if(Scene.Triangles[index]==triangle)return true;
        return false;
    }
    internal double Sole(FootProxyPose first,FootProxyPose last,int foot,double fraction)
    {
        var a0=Sole(first,foot,false);var b0=Sole(first,foot,true);
        var a1=Sole(last,foot,false);var b1=Sole(last,foot,true);
        var a=a0==a1?a0:(Range.Point(a0)+(Range.Point(a1)-Range.Point(a0))*fraction).Lo;
        var b=b0==b1?b0:(Range.Point(b0)+(Range.Point(b1)-Range.Point(b0))*fraction).Lo;
        // Nonnegative endpoint displacement independently proves this floor;
        // keep exact symbolic tangency rather than a subtraction tolerance.
        return Math.Max(Treads[foot].Height,Math.Min(a,b));
    }
}

public sealed class PairedPlantObservation
{
    public PairedPlantPolicy Policy { get; }
    public FootProxyPose Actual { get; }
    internal PairedPlantObservation(PairedPlantPolicy policy,FootProxyPose actual){Policy=policy;Actual=actual;}
}

public sealed class PairedPlantInterval
{
    public PairedPlantObservation Before { get; }
    public PairedPlantObservation After { get; }
    public MeasuredFootInterval Motion { get; }
    public PairedPlantPolicy Policy=>Before.Policy;
    private PairedPlantInterval(PairedPlantObservation before,PairedPlantObservation after,MeasuredFootInterval motion)
    {Before=before;After=after;Motion=motion;}
    public static bool TryCreate(PairedPlantObservation before,PairedPlantObservation after,double now,out PairedPlantInterval? interval)
    {
        interval=null;
        if(before is null||after is null||!ReferenceEquals(before.Policy,after.Policy)
            ||!MeasuredFootInterval.TryCreate(before.Actual,after.Actual,now,out var motion))return false;
        interval=new(before,after,motion!);return true;
    }
}

internal sealed class PlantTread
{
    internal readonly int[] Indices;
    internal readonly Vector3[] Boundary;
    internal readonly float Height;
    private PlantTread(int[] indices,Vector3[] boundary){Indices=indices;Boundary=boundary;Height=boundary[0].Y;}
    internal bool Has(int index)=>Array.IndexOf(Indices,index)>=0;
    internal static bool TryCreate(MeasuredTriangleScene scene,ReadOnlySpan<int> indices,out PlantTread? tread)
    {
        tread=null;if(indices.Length is <1 or >2)return false;
        var vertices=new List<Vector3>(4);
        foreach(var index in indices)
        {
            if((uint)index>=scene.Triangles.Length)return false;
            var t=scene.Triangles[index];
            if(t.A.Y!=t.B.Y||t.A.Y!=t.C.Y||Vector3.Cross(t.B-t.A,t.C-t.A).Y<=0)return false;
            foreach(var p in new[]{t.A,t.B,t.C})if(!vertices.Contains(p))vertices.Add(p);
        }
        if(vertices.Any(p=>p.Y!=vertices[0].Y))return false;
        if(indices.Length==2)
        {
            if(indices[0]==indices[1]||vertices.Count!=4)return false;
            var a=scene.Triangles[indices[0]];var b=scene.Triangles[indices[1]];
            var common=new[]{a.A,a.B,a.C}.Where(p=>p==b.A||p==b.B||p==b.C).ToArray();
            if(common.Length!=2)return false;
            var p=new[]{a.A,a.B,a.C}.Single(x=>x!=common[0]&&x!=common[1]);
            var q=new[]{b.A,b.B,b.C}.Single(x=>x!=common[0]&&x!=common[1]);
            if(Cross(common[0],common[1],p)*Cross(common[0],common[1],q)>=0)return false;
        }
        // Small immutable convex hull at admission only. Four corners must all
        // survive: a concave two-face union is not promoted to its convex hull.
        vertices.Sort((a,b)=>a.X!=b.X?a.X.CompareTo(b.X):a.Z.CompareTo(b.Z));
        var hull=new List<Vector3>(8);
        foreach(var p in vertices){while(hull.Count>=2&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        var lower=hull.Count;
        for(var i=vertices.Count-2;i>=0;i--){var p=vertices[i];while(hull.Count>lower&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        hull.RemoveAt(hull.Count-1);
        if(hull.Count!=vertices.Count)return false;
        tread=new(indices.ToArray(),hull.ToArray());return true;
    }
    internal bool Contains(Vector3 p,float margin=0)
    {
        for(var i=0;i<Boundary.Length;i++)
        {
            var a=Boundary[i];var b=Boundary[(i+1)%Boundary.Length];
            var dx=(double)b.X-a.X;var dz=(double)b.Z-a.Z;
            var cross=(Range.Point(p.Z)-Range.Point(a.Z))*dx-(Range.Point(p.X)-Range.Point(a.X))*dz;
            var required=Math.BitIncrement(margin*Math.BitIncrement(Math.Sqrt(dx*dx+dz*dz)));
            if(cross.Lo<required)return false;
        }
        return true;
    }
    private static double Cross(Vector3 a,Vector3 b,Vector3 c)=>((double)b.X-a.X)*((double)c.Z-a.Z)-((double)b.Z-a.Z)*((double)c.X-a.X);
}
