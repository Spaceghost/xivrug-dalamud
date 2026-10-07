using System.Numerics;

using XivCloth.Core;

namespace XivCloth.Core.Tests;

/// <summary>Finite feature witnesses generate local mass-weighted3-D contact
/// corrections. Their numerical distance is a response heuristic only: the
/// separate interval swept-support proof decides whether publication is safe.</summary>
internal static class FrozenPrismChecks
{
    private readonly record struct ClipVertex(D3 Position,D3 Barycentric);

    // Clip the cloth face by the three planes extruded from the finite support
    // edges. Unlike corner tests this finds a buried face's INTERIOR overlap.
    // This is explicitly one-sided support semantics, not solid-volume inference
    // for arbitrary model soups. Two-sided finite sheets do not use this rule.
    private static bool ProjectedMinimum(MeasuredTriangle cloth,MeasuredTriangle support,out double minimum,out D3 bary)
    {
        Span<ClipVertex> first=stackalloc ClipVertex[8];Span<ClipVertex> second=stackalloc ClipVertex[8];
        first[0]=new(new(cloth.A),new(1,0,0));first[1]=new(new(cloth.B),new(0,1,0));first[2]=new(new(cloth.C),new(0,0,1));
        Span<D3> fixedPoints=stackalloc D3[3]{new(support.A),new(support.B),new(support.C)};
        var normal=D3.Cross(fixedPoints[1]-fixedPoints[0],fixedPoints[2]-fixedPoints[0]);normal/=Math.Sqrt(normal.Squared);
        var count=3;
        for(var edge=0;edge<3;edge++)
        {
            var origin=fixedPoints[edge];var inward=D3.Cross(normal,fixedPoints[(edge+1)%3]-origin);
            inward/=Math.Sqrt(inward.Squared);var next=0;
            for(var i=0;i<count;i++)
            {
                var a=first[i];var b=first[(i+1)%count];
                var da=D3.Dot(a.Position-origin,inward)+1e-9;var db=D3.Dot(b.Position-origin,inward)+1e-9;
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
            var distance=D3.Dot(vertex.Position-fixedPoints[0],normal);
            if(distance<minimum){minimum=distance;bary=vertex.Barycentric;}
        }
        return count>0;
    }

    internal static SweepVerdict OneSidedClear(Vector3[] positions,int[] indices,MeasuredTriangleScene scene,float clearance,ref CollisionWork work)
    {
        if(scene.TwoSided)return SweepVerdict.Clear;
        foreach(var obstacle in scene.Triangles)
        {
            var origin=new D3(obstacle.A);var normal=D3.Cross(new D3(obstacle.B)-origin,new D3(obstacle.C)-origin);
            normal/=Math.Sqrt(normal.Squared);
            for(var i=0;i<indices.Length;i+=3)
            {
                if(!work.Charge(0))return SweepVerdict.BudgetExceeded;
                var face=new MeasuredTriangle(positions[indices[i]],positions[indices[i+1]],positions[indices[i+2]]);
                if(D3.Dot(new D3(face.A)-origin,normal)>=clearance&&D3.Dot(new D3(face.B)-origin,normal)>=clearance
                    &&D3.Dot(new D3(face.C)-origin,normal)>=clearance)continue;
                if(ProjectedMinimum(face,obstacle,out var distance,out _)&&distance<clearance)return SweepVerdict.Unproven;
            }
        }
        return SweepVerdict.Clear;
    }
}
