using System.Numerics;

namespace XivCloth.Core;

internal readonly record struct ContactWitness(D3 Cloth,D3 Obstacle,D3 Barycentric,double Squared);
internal readonly record struct SweptContactHint(int A,int B,int C,MeasuredTriangle Obstacle,double Time);

/// <summary>Finite feature witnesses generate local mass-weighted3-D contact
/// corrections. Their numerical distance is a response heuristic only: the
/// separate interval swept-support proof decides whether publication is safe.</summary>
internal static class TriangleContacts
{
    private readonly record struct ClipVertex(D3 Position,D3 Barycentric);

    // Clip the cloth face by the three planes extruded from the finite support
    // edges. Unlike corner tests this finds a buried face's INTERIOR overlap.
    // This is explicitly one-sided support semantics, not solid-volume inference
    // for arbitrary model soups. Two-sided finite sheets do not use this rule.
    internal static bool ProjectedMinimum(MeasuredTriangle cloth,in PreparedTriangle support,out double minimum,out D3 bary)
    {
        Span<ClipVertex> first=stackalloc ClipVertex[8];Span<ClipVertex> second=stackalloc ClipVertex[8];
        Span<double> distances=stackalloc double[8];
        first[0]=new(new(cloth.A),new(1,0,0));first[1]=new(new(cloth.B),new(0,1,0));first[2]=new(new(cloth.C),new(0,0,1));
        var normal=support.Normal;
        var count=3;
        for(var edge=0;edge<3;edge++)
        {
            var origin=support.Vertex(edge);var inward=support.Inward(edge);var next=0;
            var inside=0;
            for(var i=0;i<count;i++)
            {
                // The old loop evaluated this exact expression twice per
                // vertex (as a and the preceding b). Preserve its arithmetic.
                var distance=D3.Dot(first[i].Position-origin,inward)+1e-9;
                distances[i]=distance;if(distance>=0)inside++;
            }
            if(inside==count)continue;
            if(inside==0){count=0;break;}
            for(var i=0;i<count;i++)
            {
                var a=first[i];var b=first[(i+1)%count];
                var da=distances[i];var db=distances[(i+1)%count];
                if(da>=0)second[next++]=a;
                if((da>=0)!=(db>=0))
                {
                    var t=da/(da-db);second[next++]=new(D3.Lerp(a.Position,b.Position,t),D3.Lerp(a.Barycentric,b.Barycentric,t));
                }
            }
            var swap=first;first=second;second=swap;count=next;if(count==0)break;
        }
        minimum=double.PositiveInfinity;bary=default;
        foreach(var vertex in first[..count])
        {
            var distance=D3.Dot(vertex.Position-support.A,normal);
            if(distance<minimum){minimum=distance;bary=vertex.Barycentric;}
        }
        return count>0;
    }

    internal static SweepVerdict OneSidedClear(Vector3[] positions,int[] indices,MeasuredTriangleScene scene,float clearance,ref CollisionWork work,
        PlantContactDispatch? plant=null)
    {
        if(scene.Coverage is not null)
            foreach(var p in positions)
            {
                var guard=scene.GuardQuery(new Box3(p,p).Expand(clearance),ref work);
                if(guard<0)return guard==-2?SweepVerdict.Unproven:SweepVerdict.BudgetExceeded;
            }
        if(scene.TwoSided||scene.Triangles.IsEmpty)return SweepVerdict.Clear;
        // One bounded, counted refit of the whole material AABB. This does not
        // discard the infinite blocked side of a finite one-sided support face.
        var minimum=new Vector3(float.PositiveInfinity);var maximum=new Vector3(float.NegativeInfinity);
        foreach(var position in positions)
        {
            if(!work.Charge(2))return SweepVerdict.BudgetExceeded;
            minimum=Vector3.Min(minimum,position);maximum=Vector3.Max(maximum,position);
        }
        var bounds=new Box3(minimum,maximum);
        for(var obstacleIndex=0;obstacleIndex<scene.Triangles.Length;obstacleIndex++)
        {
            ref readonly var prepared=ref scene.Prepared(obstacleIndex);
            // Fixed-size interval plane work is charged even when no face pair
            // survives. Unproved boxes retain the original all-pairs fallback.
            if(!work.Charge(2))return SweepVerdict.BudgetExceeded;
            if(PrismBounds.ProvesClear(bounds,prepared,clearance))continue;
            var origin=prepared.A;var normal=prepared.Normal;
            for(var i=0;i<indices.Length;i+=3)
            {
                if(!work.Charge(0))return SweepVerdict.BudgetExceeded;
                var face=new MeasuredTriangle(positions[indices[i]],positions[indices[i+1]],positions[indices[i+2]]);
                if(plant?.SelectedTerrain(i/3,obstacleIndex)==true)
                {
                    if(!plant.TerrainCertificate(i/3,obstacleIndex,face,face))return SweepVerdict.Unproven;
                    continue;
                }
                if(D3.Dot(new D3(face.A)-origin,normal)>=clearance&&D3.Dot(new D3(face.B)-origin,normal)>=clearance
                    &&D3.Dot(new D3(face.C)-origin,normal)>=clearance)continue;
                if(ProjectedMinimum(face,prepared,out var distance,out _)&&distance<clearance)return SweepVerdict.Unproven;
            }
        }
        return SweepVerdict.Clear;
    }

    public static ContactWitness Closest(MeasuredTriangle cloth,MeasuredTriangle obstacle)
    {
        Span<D3> a=stackalloc D3[3]{new(cloth.A),new(cloth.B),new(cloth.C)};
        Span<D3> b=stackalloc D3[3]{new(obstacle.A),new(obstacle.B),new(obstacle.C)};
        var best=new ContactWitness(default,default,default,double.PositiveInfinity);
        for(var i=0;i<3;i++)
        {
            var q=PointTriangle(a[i],b[0],b[1],b[2],out _);Take(a[i],q,Basis(i),ref best);
            var p=PointTriangle(b[i],a[0],a[1],a[2],out var bary);Take(p,b[i],bary,ref best);
            if(Crosses(a[i],a[(i+1)%3],b[0],b[1],b[2],out var along,out var hit))
                Take(hit,hit,Basis(i)*(1-along)+Basis((i+1)%3)*along,ref best);
            if(Crosses(b[i],b[(i+1)%3],a[0],a[1],a[2],out _,out hit))
            {PointTriangle(hit,a[0],a[1],a[2],out bary);Take(hit,hit,bary,ref best);}
            for(var j=0;j<3;j++)
            {
                Segments(a[i],a[(i+1)%3],b[j],b[(j+1)%3],out var s,out var t);
                Take(D3.Lerp(a[i],a[(i+1)%3],s),D3.Lerp(b[j],b[(j+1)%3],t),
                    Basis(i)*(1-s)+Basis((i+1)%3)*s,ref best);
            }
        }
        return best;
    }
    private static D3 Basis(int i)=>i==0?new(1,0,0):i==1?new(0,1,0):new(0,0,1);
    private static void Take(D3 a,D3 b,D3 bary,ref ContactWitness best)
    {var squared=(a-b).Squared;if(squared<best.Squared)best=new(a,b,bary,squared);}
    private static bool Crosses(D3 from,D3 to,D3 a,D3 b,D3 c,out double along,out D3 at)
    {
        var n=D3.Cross(b-a,c-a);var start=D3.Dot(from-a,n);var end=D3.Dot(to-a,n);var denominator=start-end;
        along=0;at=default;if(Math.Abs(denominator)<1e-30)return false;
        along=start/denominator;if(along<0||along>1)return false;
        at=D3.Lerp(from,to,along);return Barycentric(at,a,b,c,out _);
    }
    private static D3 PointTriangle(D3 p,D3 a,D3 b,D3 c,out D3 bary)
    {
        var n=D3.Cross(b-a,c-a);var length=n.Squared;
        if(length>1e-24)
        {
            var projected=p-n*(D3.Dot(p-a,n)/length);
            if(Barycentric(projected,a,b,c,out bary))return projected;
        }
        var ab=b-a;var bc=c-b;var ca=a-c;
        var s=ab.Squared>1e-30?Math.Clamp(D3.Dot(p-a,ab)/ab.Squared,0,1):0;
        var q=a+ab*s;bary=new(1-s,s,0);var best=(p-q).Squared;
        var t=bc.Squared>1e-30?Math.Clamp(D3.Dot(p-b,bc)/bc.Squared,0,1):0;
        var r=b+bc*t;if((p-r).Squared<best){q=r;best=(p-r).Squared;bary=new(0,1-t,t);}
        var u=ca.Squared>1e-30?Math.Clamp(D3.Dot(p-c,ca)/ca.Squared,0,1):0;
        r=c+ca*u;if((p-r).Squared<best){q=r;bary=new(u,0,1-u);}return q;
    }
    private static bool Barycentric(D3 p,D3 a,D3 b,D3 c,out D3 bary)
    {
        var u=b-a;var v=c-a;var q=p-a;var n=D3.Cross(u,v);
        double denominator,s,t;
        if(Math.Abs(n.X)>=Math.Abs(n.Y)&&Math.Abs(n.X)>=Math.Abs(n.Z))
        {denominator=u.Y*v.Z-u.Z*v.Y;s=q.Y*v.Z-q.Z*v.Y;t=u.Y*q.Z-u.Z*q.Y;}
        else if(Math.Abs(n.Y)>=Math.Abs(n.Z))
        {denominator=u.X*v.Z-u.Z*v.X;s=q.X*v.Z-q.Z*v.X;t=u.X*q.Z-u.Z*q.X;}
        else{denominator=u.X*v.Y-u.Y*v.X;s=q.X*v.Y-q.Y*v.X;t=u.X*q.Y-u.Y*q.X;}
        bary=default;if(Math.Abs(denominator)<1e-24)return false;
        s/=denominator;t/=denominator;if(s<0||t<0||s+t>1)return false;
        bary=new(1-s-t,s,t);return true;
    }
    private static void Segments(D3 a,D3 b,D3 c,D3 d,out double s,out double t)
    {
        var u=b-a;var v=d-c;var r=a-c;var uu=u.Squared;var vv=v.Squared;var uv=D3.Dot(u,v);
        var ur=D3.Dot(u,r);var vr=D3.Dot(v,r);
        if(uu<1e-24){s=0;t=vv>1e-24?Math.Clamp(vr/vv,0,1):0;return;}
        if(vv<1e-24){t=0;s=Math.Clamp(-ur/uu,0,1);return;}
        var denominator=uu*vv-uv*uv;s=denominator>1e-24?Math.Clamp((uv*vr-ur*vv)/denominator,0,1):0;
        t=(uv*s+vr)/vv;
        if(t<0){t=0;s=Math.Clamp(-ur/uu,0,1);}else if(t>1){t=1;s=Math.Clamp((uv-ur)/uu,0,1);}
    }

    internal static bool Project(Vector3[] positions,Vector3[] previous,int[] indices,float[] weights,
        MeasuredTriangleScene scene,float thickness,ref CollisionWork work,FaceQueryCache? queries=null,PlantContactDispatch? plant=null)
    {
        // Match the original float squared cutoff as well as actual distance.
        // Extra1e-9 only reduces pruning near uncertain legacy witness roundoff.
        var responseDistance=Math.Max(thickness,Math.BitIncrement(Math.Sqrt(thickness*thickness)))+1e-9;
        Span<int> candidates=stackalloc int[MeasuredTriangleScene.MaximumTriangles];
        for(var f=0;f<indices.Length;f+=3)
        {
            var a=indices[f];var b=indices[f+1];var c=indices[f+2];
            var face=new MeasuredTriangle(positions[a],positions[b],positions[c]);
            var box=Box3.Triangle(face).Expand(scene.TwoSided?thickness:XpbdCloth.MaximumPenetration);
            var count=queries==null?scene.Query(box,candidates,ref work):queries.Query(f/3,scene,box,candidates,ref work);if(count<0)return false;
            var deferredDeep=false;
            foreach(var j in candidates[..count])
            {
                if(!work.Charge(0))return false;
                // Joint paired response already handled these actual pairs.
                // Final finite-plane certificate remains mandatory below.
                if(plant?.SelectedTerrain(f/3,j)==true)continue;
                face=new(positions[a],positions[b],positions[c]);var fixedFace=scene.Triangles[j];D3 normal,bary;double distance;
                ref readonly var prepared=ref scene.Prepared(j);
                // Cheap response-only rejection before feature witnesses. A
                // triangle wholly beyond the target support plane needs no
                // correction. This is not the acceptance proof: the final
                // interval sweep still independently certifies separation.
                var origin=prepared.A;var plane=prepared.Normal;
                var da=D3.Dot(new D3(face.A)-origin,plane);var db=D3.Dot(new D3(face.B)-origin,plane);var dc=D3.Dot(new D3(face.C)-origin,plane);
                if(da>=thickness&&db>=thickness&&dc>=thickness)continue;
                if(scene.TwoSided&&da<=-thickness&&db<=-thickness&&dc<=-thickness)continue;
                if(!scene.TwoSided&&ProjectedMinimum(face,prepared,out distance,out bary)&&distance<thickness)
                {
                    if(distance < -XpbdCloth.MaximumPenetration){deferredDeep=true;continue;}
                    normal=prepared.Normal;
                }
                else
                {
                    // One-sided prism and deep-contact policy has ALREADY run.
                    // A finite face farther than response range needs no closest
                    // feature correction. Every candidate remains charged and
                    // final endpoint/continuous/foot certificates still run.
                    if(prepared.SeparatesFinite(face,responseDistance))continue;
                    var witness=Closest(face,fixedFace);if(witness.Squared>=thickness*thickness)continue;
                    distance=Math.Sqrt(witness.Squared);bary=witness.Barycentric;
                    if(distance>1e-10)normal=(witness.Cloth-witness.Obstacle)/distance;
                    else
                    {
                        var before=Closest(new(previous[a],previous[b],previous[c]),fixedFace);
                        if(before.Squared<=1e-20)return false; // unknown side; do not guess
                        normal=(before.Cloth-before.Obstacle)/Math.Sqrt(before.Squared);
                    }
                }
                var denominator=weights[a]*bary.X*bary.X+weights[b]*bary.Y*bary.Y+weights[c]*bary.Z*bary.Z;
                if(denominator<1e-20)return false;
                var delta=(thickness-distance)/denominator;
                positions[a]+=(normal*(delta*weights[a]*bary.X)).Float;
                positions[b]+=(normal*(delta*weights[b]*bary.Y)).Float;
                positions[c]+=(normal*(delta*weights[c]*bary.Z)).Float;
                // A later candidate must not query from a corrected face that
                // has left the captured volume, even if final guards would
                // eventually refuse it. All state is rolled back by Advance.
                if(scene.Coverage is not null&&!scene.Covers(Box3.Triangle(new(positions[a],positions[b],positions[c]))
                    .Expand(scene.TwoSided?thickness:XpbdCloth.MaximumPenetration)))return false;
            }
            // As with point contacts, a later shallow tread can clear the
            // same face from an earlier riser's footprint. Deep pairs receive
            // no correction. Recheck the complete original candidate list at
            // the corrected pose, charging every pair, and refuse any remaining
            // deep contact. New contacts outside this query are still subject
            // to the final independent whole-face admission and swept guard.
            if(deferredDeep)foreach(var j in candidates[..count])
            {
                if(!work.Charge(0))return false;
                face=new(positions[a],positions[b],positions[c]);
                if(ProjectedMinimum(face,scene.Prepared(j),out var distance,out _)
                    &&distance < -XpbdCloth.MaximumPenetration)
                    return false;
            }
        }
        return true;
    }

    internal static SweepVerdict Sweep(Vector3[] before,Vector3[] after,int[] indices,MeasuredTriangleScene scene,
        float clearance,ref CollisionWork work)
        =>Sweep(before,after,indices,scene,clearance,ref work,out _);
    internal static SweepVerdict Sweep(Vector3[] before,Vector3[] after,int[] indices,MeasuredTriangleScene scene,
        float clearance,ref CollisionWork work,out SweptContactHint hint,FaceQueryCache? queries=null,PlantContactDispatch? plant=null)
    {
        hint=default;
        Span<int> candidates=stackalloc int[MeasuredTriangleScene.MaximumTriangles];
        for(var f=0;f<indices.Length;f+=3)
        {
            var a=indices[f];var b=indices[f+1];var c=indices[f+2];
            var first=new MeasuredTriangle(before[a],before[b],before[c]);var last=new MeasuredTriangle(after[a],after[b],after[c]);
            var box=Box3.Union(Box3.Triangle(first),Box3.Triangle(last)).Expand(clearance);
            var count=queries==null?scene.Query(box,candidates,ref work):queries.Query(f/3,scene,box,candidates,ref work);
            if(count<0)return count==-2?SweepVerdict.Unproven:SweepVerdict.BudgetExceeded;
            foreach(var j in candidates[..count])
            {
                if(!work.Charge(0))return SweepVerdict.BudgetExceeded;
                if(plant?.SelectedTerrain(f/3,j)==true)
                {
                    if(!plant.TerrainCertificate(f/3,j,first,last))return SweepVerdict.Invalid;
                    continue;
                }
                var result=TriangleSweep.Check(first,last,scene.Prepared(j),clearance,ref work,out var time);
                if(result!=SweepVerdict.Clear)
                {
                    if(result==SweepVerdict.Unproven)hint=new(a,b,c,scene.Triangles[j],time);
                    return result;
                }
            }
        }
        return SweepVerdict.Clear;
    }

    // The unresolved slice is only a response hint, never an acceptance proof.
    // Correct its finite closest feature through the moving barycentric point;
    // all endpoint and complete swept checks must run again afterward.
    internal static bool RespondToSweep(Vector3[] before,Vector3[] after,float[] weights,float[] travel,
        SweptContactHint hint,float thickness,ref CollisionWork work)
    {
        if(!work.Charge(0)||!double.IsFinite(hint.Time)||hint.Time is <=0 or >1)return false;
        var a=hint.A;var b=hint.B;var c=hint.C;
        var face=new MeasuredTriangle(D3.Lerp(new(before[a]),new(after[a]),hint.Time).Float,
            D3.Lerp(new(before[b]),new(after[b]),hint.Time).Float,D3.Lerp(new(before[c]),new(after[c]),hint.Time).Float);
        var witness=Closest(face,hint.Obstacle);var distance=Math.Sqrt(witness.Squared);
        if(!double.IsFinite(distance)||distance>=thickness)return false;
        D3 normal;
        if(distance>1e-10)normal=(witness.Cloth-witness.Obstacle)/distance;
        else
        {
            if(!work.Charge(0))return false;
            var prior=Closest(new(before[a],before[b],before[c]),hint.Obstacle);
            if(prior.Squared<=1e-20)return false;
            normal=(prior.Cloth-prior.Obstacle)/Math.Sqrt(prior.Squared);
        }
        var bary=witness.Barycentric;
        var denominator=weights[a]*bary.X*bary.X+weights[b]*bary.Y*bary.Y+weights[c]*bary.Z*bary.Z;
        if(denominator<1e-20)return false;
        var delta=(thickness-distance)/(hint.Time*denominator);
        var da=(normal*(delta*weights[a]*bary.X)).Float;
        var db=(normal*(delta*weights[b]*bary.Y)).Float;
        var dc=(normal*(delta*weights[c]*bary.Z)).Float;
        var la=da.Length();var lb=db.Length();var lc=dc.Length();
        if(!Admitted(a,da,la)||!Admitted(b,db,lb)||!Admitted(c,dc,lc))return false;
        after[a]+=da;after[b]+=db;after[c]+=dc;travel[a]+=la;travel[b]+=lb;travel[c]+=lc;
        return true;
        bool Admitted(int vertex,Vector3 correction,float length)=>Geometry.Valid(after[vertex]+correction)
            &&float.IsFinite(length)&&length<=.025f&&travel[vertex]+length<=XpbdCloth.MaximumPenetration;
    }
}
